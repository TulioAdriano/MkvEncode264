using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Drives MakeMKV's console tool (makemkvcon) in "robot" mode to scan a DVD source and rip
/// titles to MKV. Robot-mode output is line based: <c>KIND:field,field,"quoted string",...</c>
/// (see https://www.makemkv.com/developers/usage.txt).
/// </summary>
static class MakeMkv
{
    // Robot-mode item attribute ids (ap_iaXxx in MakeMKV's apdefs.h) used by the scanner.
    const int AttrName          = 2;
    const int AttrChapterCount  = 8;
    const int AttrDuration      = 9;
    const int AttrDiskSize      = 10;
    const int AttrDiskSizeBytes = 11;
    const int AttrSourceFile    = 16;
    const int AttrOriginalId    = 24;
    const int AttrOutputFile    = 27;

    // "This application version is too old. Please download the latest version ... or enter a registration key"
    const int MsgVersionTooOld  = 5021;

    /// <summary>True if <paramref name="dir"/> is a DVD folder (contains VIDEO_TS) or is the VIDEO_TS folder itself.</summary>
    public static bool IsDvdFolder(string dir) => Directory.Exists(Path.Combine(DvdRoot(dir), "VIDEO_TS"));

    /// <summary>Normalises a DVD folder path: strips trailing separators and steps out of a VIDEO_TS folder.</summary>
    public static string DvdRoot(string dir)
    {
        dir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(dir).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir) ?? dir;
        return dir;
    }

    /// <summary>makemkvcon source specification for an ISO image or a DVD folder.</summary>
    public static string SourceSpec(string path) =>
        Directory.Exists(path) ? $"file:{path}" : $"iso:{path}";

    /// <summary>Finds makemkvcon: explicit override, then PATH, then the default install locations.</summary>
    public static string? Locate(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;

        string[] names = OperatingSystem.IsWindows()
            ? ["makemkvcon64.exe", "makemkvcon.exe"]
            : ["makemkvcon"];

        var dirs = new List<string>();
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        dirs.AddRange(pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('"')));

        if (OperatingSystem.IsWindows())
        {
            foreach (string variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramW6432" })
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } programFiles)
                    dirs.Add(Path.Combine(programFiles, "MakeMKV"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/Applications/MakeMKV.app/Contents/MacOS");
        }
        else
        {
            dirs.AddRange(["/usr/bin", "/usr/local/bin", "/opt/makemkv/bin"]);
        }

        foreach (string dir in dirs)
            foreach (string name in names)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }

    /// <summary>Runs <c>makemkvcon info</c> and returns the titles MakeMKV offers for the source.</summary>
    public static async Task<List<DvdTitle>> ScanAsync(string exe, string source, int minLength, bool verbose)
    {
        var  attrs      = new SortedDictionary<int, Dictionary<int, string>>();
        int? titleCount = null;

        var result = await RunAsync(exe, [$"--minlength={minLength}", "info", source], verbose, (kind, f) =>
        {
            switch (kind)
            {
                case "TCOUNT" when f.Count >= 1 && int.TryParse(f[0], out int count):
                    titleCount = count;
                    break;

                // TINFO:<title id>,<attribute id>,<message code>,"<value>"
                case "TINFO" when f.Count >= 4 && int.TryParse(f[0], out int id) && int.TryParse(f[1], out int attr):
                    if (!attrs.TryGetValue(id, out var dict)) attrs[id] = dict = [];
                    dict[attr] = f[3];
                    break;
            }
        });

        if (result.ExitCode != 0 || (titleCount is null && attrs.Count == 0))
            throw new MakeMkvException(result.Describe($"makemkvcon info failed (exit code {result.ExitCode})"));

        // DVD titles usually carry no name (attribute 2); show the DVD title number instead.
        return [..attrs.Select(kv => new DvdTitle(
            Id:         kv.Key,
            Name:       kv.Value.GetValueOrDefault(AttrName)
                        ?? (ParseInt(kv.Value.GetValueOrDefault(AttrOriginalId)) is int number ? $"Title {number}" : ""),
            Chapters:   ParseInt(kv.Value.GetValueOrDefault(AttrChapterCount)) ?? 0,
            Duration:   ParseDuration(kv.Value.GetValueOrDefault(AttrDuration)),
            Size:       kv.Value.GetValueOrDefault(AttrDiskSize, ""),
            SizeBytes:  ParseLong(kv.Value.GetValueOrDefault(AttrDiskSizeBytes)) ?? 0,
            SourceFile: kv.Value.GetValueOrDefault(AttrSourceFile, ""),
            OutputFile: kv.Value.GetValueOrDefault(AttrOutputFile, $"title_t{kv.Key:D2}.mkv"),
            OriginalId: ParseInt(kv.Value.GetValueOrDefault(AttrOriginalId))))];
    }

    /// <summary>Rips one title into <paramref name="destDir"/> and returns the path of the MKV written.</summary>
    public static async Task<string> RipTitleAsync(
        string exe, string source, DvdTitle title, int minLength, string destDir, bool verbose, Action<double>? onProgress)
    {
        Directory.CreateDirectory(destDir);
        foreach (string stale in Directory.GetFiles(destDir, "*.mkv"))   // leftovers from an interrupted run
            File.Delete(stale);

        // Title ids are positions in the list produced with the same --minlength, so pass it again here.
        string[] commandArgs = [$"--minlength={minLength}", "mkv", source, title.Id.ToString(CultureInfo.InvariantCulture), destDir];
        var result = await RunAsync(exe, commandArgs, verbose, (kind, f) =>
        {
            // PRGV:<current operation>,<total>,<max>
            if (kind == "PRGV" && onProgress is not null && f.Count >= 3
                && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double total)
                && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double max) && max > 0)
                onProgress(Math.Clamp(total / max, 0, 1));
        });

        string  expected = Path.Combine(destDir, title.OutputFile);
        string? produced = File.Exists(expected)
            ? expected
            : Directory.GetFiles(destDir, "*.mkv").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

        if (result.ExitCode != 0 || result.ReportedFailure || produced is null || new FileInfo(produced).Length == 0)
            throw new MakeMkvException(result.Describe($"makemkvcon mkv failed for title {title.Id} (exit code {result.ExitCode})"));

        return produced;
    }

    // ── process plumbing ─────────────────────────────────────────────────────

    sealed record RobotResult(int ExitCode, List<string> Messages, bool VersionTooOld, string StdErr)
    {
        /// <summary>MakeMKV can exit 0 yet report "Copy complete. 0 titles saved, 1 failed."</summary>
        public bool ReportedFailure => Messages.Any(m =>
            m.StartsWith("Failed to save title", StringComparison.OrdinalIgnoreCase)
            || (m.StartsWith("Copy complete", StringComparison.OrdinalIgnoreCase)
                && Regex.Match(m, @"(\d+) failed") is { Success: true } match
                && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) > 0));

        public string Describe(string headline)
        {
            var sb = new StringBuilder(headline);
            foreach (string m in Messages.TakeLast(4)) sb.Append("\n       ").Append(m);
            if (!string.IsNullOrWhiteSpace(StdErr)) sb.Append("\n       ").Append(StdErr.Trim());
            if (VersionTooOld)
                sb.Append("\n       Hint: install the latest MakeMKV from https://www.makemkv.com/download/ " +
                          "or enter a registration key with: makemkvcon reg <key>");
            return sb.ToString();
        }
    }

    static async Task<RobotResult> RunAsync(
        string exe, IEnumerable<string> commandArgs, bool verbose, Action<string, List<string>> onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-r");                   // robot mode: machine-readable output
        psi.ArgumentList.Add("--noscan");             // do not enumerate optical drives; we only read images/folders
        psi.ArgumentList.Add("--cache=512");          // read cache in MB (MakeMKV's recommendation for DVD)
        psi.ArgumentList.Add("--messages=-stdout");
        psi.ArgumentList.Add("--progress=-same");     // progress lines interleaved with messages on stdout
        foreach (string a in commandArgs) psi.ArgumentList.Add(a);

        if (verbose)
            Console.WriteLine($"> {Quote(exe)} {string.Join(' ', psi.ArgumentList.Select(Quote))}");

        using var proc = Process.Start(psi)
            ?? throw new MakeMkvException($"Failed to launch {exe}.");
        ChildProcess.Current = proc;

        var  stderrTask = proc.StandardError.ReadToEndAsync();
        var  messages   = new List<string>();
        bool tooOld     = false;

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
        {
            if (verbose) Console.WriteLine(line);

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string kind   = line[..colon];
            var    fields = ParseFields(line[(colon + 1)..]);

            // MSG:<code>,<flags>,<param count>,"<message>","<format>",<params...>
            if (kind == "MSG" && fields.Count >= 4)
            {
                messages.Add(fields[3]);
                if (fields[0] == MsgVersionTooOld.ToString(CultureInfo.InvariantCulture)) tooOld = true;
            }

            onLine(kind, fields);
        }

        await proc.WaitForExitAsync();
        ChildProcess.Current = null;
        return new RobotResult(proc.ExitCode, messages, tooOld, await stderrTask);
    }

    /// <summary>Splits a robot-mode payload on commas, honouring double-quoted strings with backslash escapes.</summary>
    internal static List<string> ParseFields(string payload)
    {
        var  fields   = new List<string>();
        var  sb       = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < payload.Length; i++)
        {
            char c = payload[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < payload.Length) sb.Append(payload[++i]);
                else if (c == '"')                       inQuotes = false;
                else                                     sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else               sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    static int?  ParseInt(string? s)  => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)   ? v : null;
    static long? ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    /// <summary>Parses MakeMKV's "H:MM:SS" (or "MM:SS") durations.</summary>
    static TimeSpan ParseDuration(string? s)
    {
        if (string.IsNullOrEmpty(s)) return TimeSpan.Zero;
        double seconds = 0;
        foreach (string part in s.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return TimeSpan.Zero;
            seconds = seconds * 60 + v;
        }
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>One title as reported by makemkvcon. <see cref="Id"/> is the id to pass to <c>makemkvcon mkv</c>.</summary>
record DvdTitle(
    int Id, string Name, int Chapters, TimeSpan Duration, string Size, long SizeBytes,
    string SourceFile, string OutputFile, int? OriginalId);

class MakeMkvException(string message) : Exception(message);
