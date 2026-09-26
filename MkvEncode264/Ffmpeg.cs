using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

record ChapterInfo(double Start, double End, string Title);

/// <summary>ffprobe / ffmpeg helpers. Both tools are expected on PATH.</summary>
static class Ffmpeg
{
    public static async Task<List<ChapterInfo>> GetChaptersAsync(string inputFile, bool verbose)
    {
        var root = await ProbeJsonAsync(inputFile, "-show_chapters", verbose);
        var arr  = root?["chapters"]?.AsArray() ?? [];

        return [..arr.Select(c =>
        {
            double start = double.Parse(c!["start_time"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            double end   = double.Parse(c!["end_time"]!.GetValue<string>(),   CultureInfo.InvariantCulture);
            string title = c["tags"]?["title"]?.GetValue<string>() ?? string.Empty;
            return new ChapterInfo(start, end, title);
        })];
    }

    public static async Task<double> GetDurationAsync(string inputFile, bool verbose)
    {
        var root = await ProbeJsonAsync(inputFile, "-show_format", verbose);
        string? duration = root?["format"]?["duration"]?.GetValue<string>();
        return duration is null
            ? throw new Exception("ffprobe did not report a duration.")
            : double.Parse(duration, CultureInfo.InvariantCulture);
    }

    /// <summary>Copies or re-encodes one time span of the input into a new MKV, keeping every stream.</summary>
    public static async Task ExtractAsync(
        string inputFile, ChapterInfo span, string outputFile, Options o, string encoder, Action<double>? onProgress)
    {
        double duration = span.End - span.Start;

        var psi = new ProcessStartInfo
        {
            FileName              = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
        };

        // Fast seek before -i, then limit to the span duration with -t (relative to the seek point)
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(span.Start.ToString("F6", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");  psi.ArgumentList.Add(inputFile);
        psi.ArgumentList.Add("-t");  psi.ArgumentList.Add(duration  .ToString("F6", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");   // preserve all streams (all audio tracks, subtitles, etc.)

        if (o.Deinterlace)
        {
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("yadif");
        }

        if (o.Encode)
        {
            if (encoder == "h264_nvenc")
            {
                // Quality-based VBR; original dimensions and framerate are preserved by default
                psi.ArgumentList.Add("-c:v");    psi.ArgumentList.Add("h264_nvenc");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("p6");
                psi.ArgumentList.Add("-rc:v");   psi.ArgumentList.Add("vbr");
                psi.ArgumentList.Add("-cq:v");   psi.ArgumentList.Add(o.Cq.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-b:v");    psi.ArgumentList.Add("0");
            }
            else
            {
                // CPU fallback: libx264, CRF uses the same 0-51 scale as NVENC CQ
                psi.ArgumentList.Add("-c:v");    psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("slow");
                psi.ArgumentList.Add("-crf");    psi.ArgumentList.Add(o.Cq.ToString(CultureInfo.InvariantCulture));
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

        Task stderrTask = o.Verbose
            ? proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())
            : ReadProgressAsync(proc.StandardError, duration, onProgress);

        await Task.WhenAll(proc.WaitForExitAsync(), stderrTask);
        ChildProcess.Current = null;

        if (proc.ExitCode != 0)
            throw new Exception($"ffmpeg exited with code {proc.ExitCode}.");
    }

    /// <summary>"h264_nvenc" when FFmpeg lists the NVENC encoder, otherwise "libx264".</summary>
    public static async Task<string> DetectEncoderAsync()
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

        // Check both streams — FFmpeg may write the encoder list to either depending on version/platform
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();

        string output = stdoutTask.Result + stderrTask.Result;
        return output.Contains("h264_nvenc") ? "h264_nvenc" : "libx264";
    }

    static async Task<JsonNode?> ProbeJsonAsync(string inputFile, string showOption, bool verbose)
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
}

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
