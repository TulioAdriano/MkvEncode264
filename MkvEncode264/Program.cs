using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// ── argument parsing ──────────────────────────────────────────────────────────
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

string  inputPath     = args[0];
int     startEp       = 1;
bool    encode        = false;
int     cq            = 20;
bool    verbose       = false;
int?    chaptersPerEp = null;       // --chapters-per-ep <N>
bool    chaptersAuto  = false;      // --chapters-per-ep auto
int?    episodes      = null;       // --episodes <N>
bool    deinterlace   = false;
string? show          = null;       // --show <name>: name files after the show, episode titles from TVmaze
string  titleSpec     = "auto";     // DVD sources only
bool    listTitles    = false;      // DVD sources only
bool    keepMkv       = false;      // DVD sources only
int     minLength     = 120;        // DVD sources only; seconds (MakeMKV's own default)
string? makeMkvPath   = null;       // DVD sources only

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--start-ep" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out startEp) || startEp < 1)
            { Console.Error.WriteLine("--start-ep must be a positive integer."); return 1; }
            break;

        case "--encode":
            encode = true;
            break;

        case "--chapters-per-ep" when i + 1 < args.Length:
            string perEpText = args[++i];
            if (perEpText.Equals("auto", StringComparison.OrdinalIgnoreCase))
            { chaptersAuto = true; chaptersPerEp = null; }
            else if (int.TryParse(perEpText, out int perEp) && perEp >= 1)
            { chaptersPerEp = perEp; chaptersAuto = false; }
            else
            { Console.Error.WriteLine("--chapters-per-ep must be a positive integer or 'auto'."); return 1; }
            break;

        case "--episodes" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out int episodeCount) || episodeCount < 1)
            { Console.Error.WriteLine("--episodes must be a positive integer."); return 1; }
            episodes = episodeCount;
            break;

        case "--show" when i + 1 < args.Length:
            show = args[++i].Trim();
            if (show.Length == 0) { Console.Error.WriteLine("--show needs a show name."); return 1; }
            break;

        case "--cq" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out cq) || cq is < 0 or > 51)
            { Console.Error.WriteLine("--cq must be between 0 and 51."); return 1; }
            break;

        case "--verbose":
            verbose = true;
            break;

        case "--deinterlace":
            deinterlace = true;
            break;

        case "--title" when i + 1 < args.Length:
            titleSpec = args[++i].Trim().ToLowerInvariant();
            if (!IsValidTitleSpec(titleSpec))
            { Console.Error.WriteLine("--title must be 'auto', 'longest', 'all', or title ids such as 0, 1-3 or 0,2,5."); return 1; }
            break;

        case "--list-titles":
            listTitles = true;
            break;

        case "--keep-mkv":
            keepMkv = true;
            break;

        case "--min-length" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out minLength) || minLength < 0)
            { Console.Error.WriteLine("--min-length must be zero or a positive number of seconds."); return 1; }
            break;

        case "--makemkv" when i + 1 < args.Length:
            makeMkvPath = args[++i];
            break;

        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintHelp();
            return 1;
    }
}

// ── input classification ──────────────────────────────────────────────────────
bool isDvd;
if (Directory.Exists(inputPath))
{
    if (!MakeMkv.IsDvdFolder(inputPath))
    { Console.Error.WriteLine($"Not a DVD folder (no VIDEO_TS inside): {inputPath}"); return 1; }
    isDvd = true;
}
else if (File.Exists(inputPath))
{
    isDvd = Path.GetExtension(inputPath).Equals(".iso", StringComparison.OrdinalIgnoreCase);
}
else
{
    Console.Error.WriteLine($"File not found: {inputPath}");
    return 1;
}

if (!isDvd && (listTitles || keepMkv || titleSpec != "auto" || minLength != 120 || makeMkvPath is not null))
    Console.WriteLine("Note: DVD options (--title, --list-titles, --keep-mkv, --min-length, --makemkv) are ignored for MKV input.");

if (deinterlace && !encode)
{
    Console.WriteLine("Note: --deinterlace requires encoding; --encode enabled automatically.");
    encode = true;
}

string encoder = "h264_nvenc";
if (encode && !listTitles)
{
    encoder = await DetectVideoEncoderAsync();
    if (encoder != "h264_nvenc")
        Console.WriteLine($"Note: NVENC not available, falling back to {encoder} (CPU encoding).");
}

// ── output location and naming ────────────────────────────────────────────────
// Episodes are written next to the source as "{base name} - EP{NN}[ - {episode title}].mkv".
string sourcePath = isDvd && Directory.Exists(inputPath) ? MakeMkv.DvdRoot(inputPath) : Path.GetFullPath(inputPath);
string outputDir  = Path.GetDirectoryName(sourcePath) ?? sourcePath;
string baseName   = Directory.Exists(sourcePath) ? Path.GetFileName(sourcePath) : Path.GetFileNameWithoutExtension(sourcePath);

Console.WriteLine($"Input:    {Path.GetFileName(sourcePath)}{(isDvd ? Directory.Exists(sourcePath) ? " (DVD folder)" : " (DVD image)" : "")}");

ShowInfo? showInfo = null;
if (show is not null)
{
    baseName = ShowLookup.SafeFileName(show);
    if (!listTitles)
    {
        showInfo = await ShowLookup.FetchAsync(show, verbose, message => Console.WriteLine($"Note: {message}"));
        if (showInfo is not null)
        {
            baseName = ShowLookup.SafeFileName(showInfo.Name);
            string year = showInfo.Premiered is { Length: >= 4 } p ? $" ({p[..4]})" : "";
            Console.WriteLine($"Show:     {showInfo.Name}{year}, {showInfo.Episodes.Count} episode titles from TVmaze");
        }
    }
}

Func<int, string> fileNameFor = epNum =>
{
    string? title = showInfo?.TitleOf(epNum);
    return title is null
        ? $"{baseName} - EP{epNum:D2}.mkv"
        : $"{baseName} - EP{epNum:D2} - {ShowLookup.SafeFileName(title)}.mkv";
};

string? tempDir = null;   // MakeMKV work folder; removed when we finish or get interrupted
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\nInterrupted, cleaning up...");
    ChildProcess.KillCurrent();
    TryDeleteDirectory(tempDir);
    Environment.Exit(130);
};

// ── MKV input: split by chapters ──────────────────────────────────────────────
if (!isDvd)
{
    List<ChapterInfo> chapters;
    try   { chapters = await GetChaptersAsync(sourcePath, verbose); }
    catch (Exception ex) { Console.Error.WriteLine($"ffprobe error: {ex.Message}"); return 1; }

    if (chapters.Count == 0)
    {
        Console.Error.WriteLine("No chapters found in the input file.");
        return 1;
    }

    var groups = EpisodePlanner.Groups(SplitMode.FixedDefault, null, chapters, chaptersPerEp, chaptersAuto, episodes, out string reason);
    Console.WriteLine($"Chapters: {chapters.Count}; {reason}");
    Console.WriteLine($"Start EP: {startEp:D2}");
    Console.WriteLine($"Mode:     {DescribeMode(encode, encoder, cq, deinterlace)}");
    Console.WriteLine();

    var (done, failed) = await SplitIntoEpisodesAsync(
        sourcePath, chapters, groups, startEp, outputDir, fileNameFor, encode, encoder, cq, deinterlace, verbose);
    return Finish(done, failed);
}

// ── DVD input: read the disc structure, rip with MakeMKV, then split ──────────
DvdStructure? dvd = null;
if (titleSpec == "auto" || listTitles)
{
    dvd = DvdStructure.TryRead(sourcePath, verbose, out string? dvdError);
    if (dvd is null)
    {
        Console.WriteLine($"Disc:     structure not readable ({dvdError}); relying on MakeMKV's title list");
    }
    else
    {
        Console.WriteLine($"Disc:     {dvd.VolumeLabel}: {dvd.Titles.Count} title(s), {dvd.Screens.Count} menu screen(s), {dvd.ButtonCount} button target(s)");
        if (listTitles)
        {
            Console.WriteLine("            Title  Length    Ch  VTS  Cells  Angles");
            foreach (IfoTitle t in dvd.Titles)
                Console.WriteLine($"            {t.Number,5}  {t.Duration,-8:h\\:mm\\:ss}  {t.Chapters,2}  {t.Vts,3}  {t.Cells,5}  {t.Angles,6}");
            foreach (MenuScreen s in dvd.Screens)
                Console.WriteLine($"            {s.Source}: {string.Join(" ", s.Targets)}");
        }
    }
}

string? makemkvcon = MakeMkv.Locate(makeMkvPath);
if (makemkvcon is null)
{
    Console.Error.WriteLine(makeMkvPath is null
        ? "makemkvcon not found. Install MakeMKV (https://www.makemkv.com) or pass --makemkv <path to makemkvcon>."
        : $"makemkvcon not found at: {makeMkvPath}");
    return 1;
}

string source = MakeMkv.SourceSpec(sourcePath);
Console.WriteLine($"MakeMKV:  {makemkvcon}");
if (!verbose) Console.Write("Scanning disc, this can take a minute...");

List<DvdTitle> titles;
try   { titles = await MakeMkv.ScanAsync(makemkvcon, source, minLength, verbose); }
catch (Exception ex) { Console.Error.WriteLine($"\rMakeMKV scan failed: {ex.Message}"); return 1; }

Console.WriteLine($"\rTitles:   {titles.Count} (at least {minLength} s long){new string(' ', 20)}");
if (titles.Count > 0)
{
    Console.WriteLine("            Id  Length    Ch  Size      Name");
    foreach (DvdTitle t in titles)
        Console.WriteLine($"            {t.Id,2}  {t.Duration,-8:h\\:mm\\:ss}  {t.Chapters,2}  {t.Size,-8}  {t.Name}");
}

if (titles.Count == 0)
{
    Console.Error.WriteLine("No titles found on the disc (try a smaller --min-length).");
    return 1;
}

// ── plan: which titles, and how each one is cut ───────────────────────────────
var            notes = new List<string>();
List<TitleJob> jobs;
switch (titleSpec)
{
    case "auto":
        jobs = EpisodePlanner.Auto(titles, dvd, notes);
        break;

    case "longest":
        DvdTitle longest = EpisodePlanner.Longest(titles);
        notes.Add($"longest title {longest.Id}; episodes are cut from its chapters");
        jobs = [new TitleJob(longest, SplitMode.AutoChapters, null)];
        break;

    default:
        if (!TryResolveTitles(titleSpec, titles, out List<DvdTitle> selected, out string? selectionError))
        {
            Console.Error.WriteLine(selectionError);
            return 1;
        }
        bool multi      = selected.Count > 1;
        bool overridden = chaptersPerEp is not null || chaptersAuto || episodes is not null;
        notes.Add(multi
            ? $"titles {string.Join(", ", selected.Select(t => t.Id))}{(overridden ? "" : "; one episode per title")}"
            : $"title {selected[0].Id}{(overridden ? "" : "; episodes are cut from its chapters")}");
        jobs = selected.Select(t => new TitleJob(t, multi ? SplitMode.WholeTitle : SplitMode.AutoChapters, null)).ToList();
        break;
}

if (chaptersPerEp is int n) notes.Add($"{n} chapters per episode (--chapters-per-ep)");
else if (chaptersAuto)      notes.Add("chapter pattern detected after ripping (--chapters-per-ep auto)");
else if (episodes is int e) notes.Add($"{e} episodes per title (--episodes)");

Console.WriteLine($"Plan:     {string.Join("; ", notes)}");
foreach (TitleJob job in jobs)
{
    string how = chaptersPerEp is not null || chaptersAuto || episodes is not null ? "" : job.Mode switch
    {
        SplitMode.WholeTitle    => ": one episode",
        SplitMode.ChapterRanges => $": {job.Ranges!.Count} episodes starting at chapters {string.Join(", ", job.Ranges.Select(r => r.First))}",
        _                       => ": chapters grouped after ripping",
    };
    Console.WriteLine($"          rip title {job.Title.Id} ({job.Title.Duration:h\\:mm\\:ss}, {job.Title.Chapters} ch){how}");
    if (listTitles && dvd is not null && EpisodePlanner.IfoFor(job.Title, dvd) is { ChapterLengths.Count: > 0 } ifo)
        Console.WriteLine($"            chapters: {DescribeChapters(ifo.ChapterLengths, job.Mode == SplitMode.ChapterRanges ? job.Ranges : null)}");
}

if (listTitles)
{
    Console.WriteLine("\nRun again without --list-titles to rip, or pick titles with --title <id>, --title all, or --title longest.");
    return 0;
}

Console.WriteLine($"Start EP: {startEp:D2}");
Console.WriteLine($"Mode:     {DescribeMode(encode, encoder, cq, deinterlace)}{(keepMkv ? ", keeping MakeMKV output" : "")}");
Console.WriteLine();

tempDir = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(sourcePath)}.makemkv-tmp");
int epNum = startEp, totalDone = 0, totalFailed = 0;
try
{
    for (int i = 0; i < jobs.Count; i++)
    {
        TitleJob job    = jobs[i];
        DvdTitle title  = job.Title;
        string   prefix = $"  [{i + 1}/{jobs.Count}] Ripping title {title.Id} ({title.Duration:h\\:mm\\:ss}, {title.Chapters} ch)";
        if (verbose) Console.WriteLine($"{prefix} ...");

        string mkv;
        try
        {
            Action<double>? onProgress = verbose ? null : pct => DrawProgress(prefix, pct);
            mkv = await MakeMkv.RipTitleAsync(makemkvcon, source, title, minLength, tempDir, verbose, onProgress);
            if (!verbose) Console.WriteLine($"\r{prefix} done{new string(' ', 40)}");
        }
        catch (Exception ex)
        {
            if (!verbose) Console.Error.WriteLine($"\r{prefix} FAILED{new string(' ', 37)}");
            Console.Error.WriteLine($"    -> {ex.Message}");
            totalFailed++;
            epNum += ExpectedEpisodes(job, chaptersPerEp, episodes);   // keep numbering stable
            continue;
        }

        List<ChapterInfo> chapters;
        try
        {
            chapters = await GetChaptersAsync(mkv, verbose);
            if (chapters.Count == 0)   // no chapter markers: the whole title is one episode
                chapters = [new ChapterInfo(0, await GetDurationAsync(mkv, verbose), title.Name)];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    -> ffprobe error: {ex.Message}");
            totalFailed++;
            continue;
        }

        var groups = EpisodePlanner.Groups(job.Mode, job.Ranges, chapters, chaptersPerEp, chaptersAuto, episodes, out string reason);
        Console.WriteLine($"        {chapters.Count} chapter(s), {groups.Count} episode(s): {reason}");

        var (done, failed) = await SplitIntoEpisodesAsync(
            mkv, chapters, groups, epNum, outputDir, fileNameFor, encode, encoder, cq, deinterlace, verbose);
        epNum       += done + failed;
        totalDone   += done;
        totalFailed += failed;

        if (keepMkv)
        {
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string kept = Path.Combine(outputDir, jobs.Count > 1 ? $"{stem} - Title{title.Id:D2}.mkv" : $"{stem}.mkv");
            File.Move(mkv, kept, overwrite: true);
            Console.WriteLine($"        MakeMKV output kept as: {Path.GetFileName(kept)}");
        }
        else
        {
            File.Delete(mkv);
        }
    }
}
finally
{
    TryDeleteDirectory(tempDir);
}

return Finish(totalDone, totalFailed);

// ── helpers ───────────────────────────────────────────────────────────────────

static void PrintHelp() => Console.WriteLine("""
    MkvEncode264 – Split an MKV, or a DVD ripped with MakeMKV, into episode files.

    USAGE
      MkvEncode264 <input.mkv | disc.iso | DVD folder> [options]

    OPTIONS
      --start-ep <N>         First episode number (default: 1)
      --chapters-per-ep <N>  Chapters grouped into one episode file (default: 4 for MKV input;
                             'auto' detects the repeating chapter pattern, the default for DVD titles)
      --episodes <N>         Cut the source into N episodes with an equal number of chapters each
      --show <name>          Name files after the show and add episode titles looked up on TVmaze
      --encode               Encode video to H.264 (NVENC, or libx264 when unavailable)
      --cq <N>               Encode quality 0-51 (default: 20; lower = better)
      --deinterlace          Deinterlace video using yadif (implies --encode)
      --verbose              Print ffprobe / ffmpeg / makemkvcon output
      -h, --help             Show this help text

    DVD OPTIONS  (ISO image or folder containing VIDEO_TS; needs MakeMKV installed)
      --list-titles          Show the disc structure, MakeMKV's titles and the plan, then exit
      --title <spec>         auto (default): find the episodes from the disc menu and title lengths
                             longest: the longest title, cut by chapters
                             all, or ids such as 0, 1-3, 0,2: those titles, one episode each
      --keep-mkv             Keep the intermediate MKV produced by MakeMKV next to the source
      --min-length <sec>     Ignore titles shorter than this (default: 120)
      --makemkv <path>       Path to makemkvcon if it is not on PATH or in the default folder

    EXAMPLES
      MkvEncode264 "RurouniKenshin_Disc1.mkv"
      MkvEncode264 "RurouniKenshin_Disc2.mkv" --start-ep 5 --encode --cq 18
      MkvEncode264 "Hamtaro_Disc1.iso" --list-titles
      MkvEncode264 "Hamtaro_Disc1.iso" --show "Hamtaro" --encode --deinterlace
      MkvEncode264 "Hamtaro_Disc2.iso" --show "Hamtaro" --start-ep 5 --title 1-4
    """);

static bool IsValidTitleSpec(string spec) =>
    Regex.IsMatch(spec, @"^(auto|longest|all|\d+(-\d+)?(,\d+(-\d+)?)*)$");

/// <summary>Resolves an explicit --title value (all, or ids/ranges) against the scanned titles.</summary>
static bool TryResolveTitles(string spec, List<DvdTitle> titles, out List<DvdTitle> selected, out string? error)
{
    selected = [];
    error    = null;

    if (spec == "all")
    {
        selected.AddRange(titles);
        return true;
    }

    var ids = new List<int>();
    foreach (string part in spec.Split(','))
    {
        string[] range = part.Split('-');
        int from = int.Parse(range[0], CultureInfo.InvariantCulture);
        int to   = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : from;
        if (to < from) { error = $"Invalid title range '{part}'."; return false; }
        ids.AddRange(Enumerable.Range(from, to - from + 1));
    }

    foreach (int id in ids.Distinct())
    {
        DvdTitle? t = titles.FirstOrDefault(t => t.Id == id);
        if (t is null)
        {
            error = $"Title {id} not found on the disc (available: {string.Join(", ", titles.Select(t => t.Id))}).";
            return false;
        }
        selected.Add(t);
    }
    return true;
}

/// <summary>How many episode numbers a title would have used, so numbering stays stable when its rip fails.</summary>
static int ExpectedEpisodes(TitleJob job, int? chaptersPerEp, int? episodes)
{
    if (chaptersPerEp is int n) return Math.Max(1, (int)Math.Ceiling(job.Title.Chapters / (double)n));
    if (episodes is int e)      return e;
    return job.Mode switch
    {
        SplitMode.WholeTitle    => 1,
        SplitMode.ChapterRanges => job.Ranges?.Count ?? 1,
        _                       => Math.Max(1, (int)Math.Ceiling(job.Title.Chapters / (double)EpisodePlanner.DefaultChaptersPerEpisode)),
    };
}

/// <summary>Chapter lengths as "m:ss", with " | " where the planned episodes begin.</summary>
static string DescribeChapters(List<TimeSpan> lengths, List<(int First, int Last)>? ranges)
{
    var starts = ranges is null ? [] : ranges.Skip(1).Select(r => r.First - 1).ToHashSet();
    return string.Join(" ", lengths.Select((t, i) => (starts.Contains(i) ? "| " : "") + EpisodePlanner.Fmt(t.TotalSeconds)));
}

static string DescribeMode(bool encode, string encoder, int cq, bool deinterlace)
{
    if (!encode) return "stream copy";
    string label = encoder == "h264_nvenc" ? "NVENC" : "CPU (libx264)";
    return $"H.264/{label} (CQ {cq}){(deinterlace ? " + yadif" : "")}";
}

static int Finish(int done, int failed)
{
    if (failed > 0)
    {
        Console.Error.WriteLine($"\n{failed} episode(s) failed.");
        return 1;
    }
    Console.WriteLine($"\nAll {done} episode(s) extracted!");
    return 0;
}

static void DrawProgress(string prefix, double pct)
{
    const int barWidth = 32;
    int f = (int)(barWidth * Math.Clamp(pct, 0, 1));
    Console.Write($"\r{prefix} [{new string('#', f)}{new string('.', barWidth - f)}] {pct * 100,3:F0}%");
}

static void TryDeleteDirectory(string? dir)
{
    if (dir is null || !Directory.Exists(dir)) return;
    try { Directory.Delete(dir, recursive: true); }
    catch { /* best effort */ }
}

/// <summary>Extracts one episode per chapter group; returns how many succeeded and failed.</summary>
static async Task<(int Done, int Failed)> SplitIntoEpisodesAsync(
    string inputFile, List<ChapterInfo> chapters, List<(int First, int Last)> groups, int startEp,
    string outputDir, Func<int, string> fileNameFor, bool encode, string encoder, int cq, bool deinterlace, bool verbose)
{
    int failures = 0;

    for (int i = 0; i < groups.Count; i++)
    {
        int epNum = startEp + i;
        var (chFirst, chLast) = groups[i];

        var merged = new ChapterInfo(
            chapters[chFirst].Start,
            chapters[chLast].End,
            chapters[chFirst].Title);

        string outputFile = Path.Combine(outputDir, fileNameFor(epNum));
        string label      = chFirst == chLast ? $"ch {chFirst + 1}" : $"ch {chFirst + 1}-{chLast + 1}";

        string prefix = $"  [{i + 1}/{groups.Count}] EP{epNum:D2} ({label})";
        if (verbose) Console.WriteLine($"{prefix} -> {Path.GetFileName(outputFile)}");

        Action<double>? onProgress = verbose ? null : pct => DrawProgress(prefix, pct);

        try
        {
            await ExtractChapterAsync(inputFile, merged, outputFile, encode, encoder, cq, deinterlace, verbose, onProgress);
            if (!verbose) Console.WriteLine($"\r{prefix} done{new string(' ', 40)}");
        }
        catch (Exception ex)
        {
            if (!verbose) Console.Error.WriteLine($"\r{prefix} FAILED{new string(' ', 37)}");
            Console.Error.WriteLine($"    -> {ex.Message}");
            failures++;
        }
    }

    return (groups.Count - failures, failures);
}

static async Task<JsonNode?> FfprobeJsonAsync(string inputFile, string showOption, bool verbose)
{
    var psi = new ProcessStartInfo
    {
        FileName               = "ffprobe",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    psi.ArgumentList.Add("-v");            psi.ArgumentList.Add("quiet");
    psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
    psi.ArgumentList.Add(showOption);
    psi.ArgumentList.Add(inputFile);

    using var proc = Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to launch ffprobe. Is FFmpeg installed and in PATH?");

    var  stdoutTask = proc.StandardOutput.ReadToEndAsync();
    Task stderrTask = verbose
        ? proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())
        : (Task)proc.StandardError.ReadToEndAsync();   // drain to prevent deadlock

    await Task.WhenAll(stdoutTask, stderrTask);
    await proc.WaitForExitAsync();

    if (proc.ExitCode != 0)
        throw new Exception($"ffprobe exited with code {proc.ExitCode}.");

    return JsonNode.Parse(stdoutTask.Result);
}

static async Task<List<ChapterInfo>> GetChaptersAsync(string inputFile, bool verbose)
{
    var root = await FfprobeJsonAsync(inputFile, "-show_chapters", verbose);
    var arr  = root?["chapters"]?.AsArray() ?? [];

    return [..arr.Select(c =>
    {
        double start = double.Parse(c!["start_time"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        double end   = double.Parse(c!["end_time"]!.GetValue<string>(),   CultureInfo.InvariantCulture);
        string title = c["tags"]?["title"]?.GetValue<string>() ?? string.Empty;
        return new ChapterInfo(start, end, title);
    })];
}

static async Task<double> GetDurationAsync(string inputFile, bool verbose)
{
    var root = await FfprobeJsonAsync(inputFile, "-show_format", verbose);
    string? duration = root?["format"]?["duration"]?.GetValue<string>();
    return duration is null
        ? throw new Exception("ffprobe did not report a duration.")
        : double.Parse(duration, CultureInfo.InvariantCulture);
}

static async Task ExtractChapterAsync(
    string inputFile, ChapterInfo chapter, string outputFile,
    bool encode, string encoder, int cq, bool deinterlace, bool verbose, Action<double>? onProgress)
{
    double duration = chapter.End - chapter.Start;

    var psi = new ProcessStartInfo
    {
        FileName              = "ffmpeg",
        RedirectStandardError = true,
        UseShellExecute       = false,
        CreateNoWindow        = true,
    };

    // Fast seek before -i, then limit to chapter duration with -t (relative to seek point)
    psi.ArgumentList.Add("-y");
    psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(chapter.Start.ToString("F6", CultureInfo.InvariantCulture));
    psi.ArgumentList.Add("-i");  psi.ArgumentList.Add(inputFile);
    psi.ArgumentList.Add("-t");  psi.ArgumentList.Add(duration   .ToString("F6", CultureInfo.InvariantCulture));
    psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");   // preserve all streams (all audio tracks, subtitles, etc.)

    if (deinterlace)
    {
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("yadif");
    }

    if (encode)
    {
        if (encoder == "h264_nvenc")
        {
            // Quality-based VBR; original dimensions and framerate are preserved by default
            psi.ArgumentList.Add("-c:v");    psi.ArgumentList.Add("h264_nvenc");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("p6");
            psi.ArgumentList.Add("-rc:v");   psi.ArgumentList.Add("vbr");
            psi.ArgumentList.Add("-cq:v");   psi.ArgumentList.Add(cq.ToString());
            psi.ArgumentList.Add("-b:v");    psi.ArgumentList.Add("0");
        }
        else
        {
            // CPU fallback: libx264, CRF uses the same 0-51 scale as NVENC CQ
            psi.ArgumentList.Add("-c:v");    psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("slow");
            psi.ArgumentList.Add("-crf");    psi.ArgumentList.Add(cq.ToString());
        }
    }
    else
    {
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("copy");
    }

    psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("copy");
    psi.ArgumentList.Add("-c:s"); psi.ArgumentList.Add("copy");
    psi.ArgumentList.Add(outputFile);

    using var proc = Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to launch ffmpeg. Is FFmpeg installed and in PATH?");
    ChildProcess.Current = proc;

    Task stderrTask = verbose
        ? proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())
        : ReadProgressAsync(proc.StandardError, duration, onProgress);

    await Task.WhenAll(proc.WaitForExitAsync(), stderrTask);
    ChildProcess.Current = null;

    if (proc.ExitCode != 0)
        throw new Exception($"ffmpeg exited with code {proc.ExitCode}.");
}

static async Task<string> DetectVideoEncoderAsync()
{
    var psi = new ProcessStartInfo
    {
        FileName               = "ffmpeg",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    psi.ArgumentList.Add("-hide_banner");
    psi.ArgumentList.Add("-encoders");

    using var proc = Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to launch ffmpeg.");

    // Check both streams — FFmpeg may write encoder list to either depending on version/platform
    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();
    await Task.WhenAll(stdoutTask, stderrTask);
    await proc.WaitForExitAsync();

    string output = stdoutTask.Result + stderrTask.Result;
    return output.Contains("h264_nvenc") ? "h264_nvenc" : "libx264";
}

static async Task ReadProgressAsync(StreamReader stderr, double totalDuration, Action<double>? onProgress)
{
    var timeRx = new Regex(@"\btime=(\d+):(\d+):(\d+\.?\d*)");
    string? line;
    while ((line = await stderr.ReadLineAsync()) != null)
    {
        if (onProgress is null) continue;
        var m = timeRx.Match(line);
        if (!m.Success) continue;
        double t = int.Parse(m.Groups[1].Value) * 3600
                 + int.Parse(m.Groups[2].Value) * 60
                 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        onProgress(Math.Min(t / totalDuration, 1.0));
    }
}

record ChapterInfo(double Start, double End, string Title);

/// <summary>Tracks the external process currently running so Ctrl+C can stop it before we exit.</summary>
static class ChildProcess
{
    public static Process? Current { get; set; }

    public static void KillCurrent()
    {
        try
        {
            Process? p = Current;
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch { /* best effort */ }
    }
}
