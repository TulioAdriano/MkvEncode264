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

string inputFile     = args[0];
int    startEp       = 1;
bool   encode        = false;
int    cq            = 20;
bool   verbose       = false;
int    chaptersPerEp = 4;
bool   deinterlace   = false;

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
            if (!int.TryParse(args[++i], out chaptersPerEp) || chaptersPerEp < 1)
            { Console.Error.WriteLine("--chapters-per-ep must be a positive integer."); return 1; }
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

        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintHelp();
            return 1;
    }
}

if (!File.Exists(inputFile))
{
    Console.Error.WriteLine($"File not found: {inputFile}");
    return 1;
}

if (deinterlace && !encode)
{
    Console.WriteLine("Note: --deinterlace requires encoding; --encode enabled automatically.");
    encode = true;
}

string encoder = "h264_nvenc";
if (encode)
{
    encoder = await DetectVideoEncoderAsync();
    if (encoder != "h264_nvenc")
        Console.WriteLine($"Note: NVENC not available, falling back to {encoder} (CPU encoding).");
}

// ── chapter discovery ─────────────────────────────────────────────────────────
string outputDir = Path.GetDirectoryName(Path.GetFullPath(inputFile))!;
string baseName  = Path.GetFileNameWithoutExtension(inputFile);

List<ChapterInfo> chapters;
try   { chapters = await GetChaptersAsync(inputFile, verbose); }
catch (Exception ex) { Console.Error.WriteLine($"ffprobe error: {ex.Message}"); return 1; }

if (chapters.Count == 0)
{
    Console.Error.WriteLine("No chapters found in the input file.");
    return 1;
}

Console.WriteLine($"Input:    {Path.GetFileName(inputFile)}");
Console.WriteLine($"Chapters: {chapters.Count} ({chaptersPerEp} per episode)");
Console.WriteLine($"Start EP: {startEp:D2}");
string encoderLabel = encoder == "h264_nvenc" ? "NVENC" : "CPU (libx264)";
Console.WriteLine($"Mode:     {(encode ? $"H.264/{encoderLabel} (CQ {cq}){(deinterlace ? " + yadif" : "")}" : "stream copy")}");
Console.WriteLine();

// ── chapter extraction ────────────────────────────────────────────────────────
int epCount  = (int)Math.Ceiling(chapters.Count / (double)chaptersPerEp);
int failures = 0;
for (int i = 0; i < epCount; i++)
{
    int epNum   = startEp + i;
    int chFirst = i * chaptersPerEp;
    int chLast  = Math.Min(chFirst + chaptersPerEp - 1, chapters.Count - 1);

    var merged = new ChapterInfo(
        chapters[chFirst].Start,
        chapters[chLast].End,
        chapters[chFirst].Title);

    string outputFile = Path.Combine(outputDir, $"{baseName} - EP{epNum:D2}.mkv");
    string label      = $"ch {chFirst + 1}-{chLast + 1}";

    string prefix = $"  [{i + 1}/{epCount}] EP{epNum:D2} ({label})";
    if (verbose) Console.WriteLine($"{prefix} ...");

    Action<double>? onProgress = verbose ? null : pct =>
    {
        const int barWidth = 32;
        int f = (int)(barWidth * Math.Clamp(pct, 0, 1));
        Console.Write($"\r{prefix} [{new string('#', f)}{new string('.', barWidth - f)}] {pct * 100,3:F0}%");
    };

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

if (failures > 0) { Console.Error.WriteLine($"\n{failures} episode(s) failed."); return 1; }

Console.WriteLine($"\nAll {epCount} episode(s) extracted!");
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

static void PrintHelp() => Console.WriteLine("""
    MkvEncode264 – Split MKV chapters into individual episode files.

    USAGE
      MkvEncode264 <input.mkv> [options]

    OPTIONS
      --start-ep <N>        First episode number (default: 1)
      --chapters-per-ep <N> Chapters grouped into one episode file (default: 4)
      --encode              Encode video to H.264 via NVENC (default: stream copy)
      --cq <N>              NVENC constant quality 0-51 (default: 20; lower = better)
      --deinterlace         Deinterlace video using yadif (implies --encode)
      --verbose             Print ffprobe / ffmpeg output
      -h, --help            Show this help text

    EXAMPLES
      MkvEncode264 "RurouniKenshin_Disc1.mkv"
      MkvEncode264 "RurouniKenshin_Disc2.mkv" --start-ep 5
      MkvEncode264 "RurouniKenshin_Disc2.mkv" --start-ep 5 --encode --cq 18
      MkvEncode264 "RurouniKenshin_Disc2.mkv" --start-ep 5 --chapters-per-ep 4 --encode
    """);

static async Task<List<ChapterInfo>> GetChaptersAsync(string inputFile, bool verbose)
{
    var psi = new ProcessStartInfo
    {
        FileName              = "ffprobe",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    psi.ArgumentList.Add("-v");            psi.ArgumentList.Add("quiet");
    psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
    psi.ArgumentList.Add("-show_chapters");
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

    var root = JsonNode.Parse(stdoutTask.Result);
    var arr  = root?["chapters"]?.AsArray() ?? [];

    return [..arr.Select(c =>
    {
        double start = double.Parse(c!["start_time"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        double end   = double.Parse(c!["end_time"]!.GetValue<string>(),   CultureInfo.InvariantCulture);
        string title = c["tags"]?["title"]?.GetValue<string>() ?? string.Empty;
        return new ChapterInfo(start, end, title);
    })];
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

    Task stderrTask = verbose
        ? proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())
        : ReadProgressAsync(proc.StandardError, duration, onProgress);

    await Task.WhenAll(proc.WaitForExitAsync(), stderrTask);

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
