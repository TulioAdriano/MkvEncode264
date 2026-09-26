using System.Diagnostics;
using System.Globalization;

/// <summary>Everything decided about one source before ripping: its titles, the jobs, and why.</summary>
sealed class DiscPlan
{
    public required Source Source { get; init; }

    public DvdStructure?  Structure      { get; set; }
    public string?        StructureError { get; set; }
    public List<DvdTitle> Titles         { get; set; } = [];
    public List<TitleJob> Jobs           { get; set; } = [];
    public List<string>   Notes          { get; set; } = [];

    public List<ChapterInfo>           MkvChapters { get; set; } = [];   // MKV sources only
    public List<(int First, int Last)> MkvGroups   { get; set; } = [];   // MKV sources only

    public string? Error            { get; set; }                        // planning failed: skipped when running
    public int     ExpectedEpisodes { get; set; }
    public bool    Estimated        { get; set; }                        // count only known after ripping
    public int     FirstEpisode     { get; set; }

    public string EpisodeRange => Error is not null ? "" : (Estimated ? "~" : "") + Runner.Range(FirstEpisode, ExpectedEpisodes);
}

/// <summary>Plans each source, then rips and splits them one after another with progress output.</summary>
static class Runner
{
    static string? tempDir;   // MakeMKV work folder of the disc being processed

    public static void CleanupTemp() => TryDeleteDirectory(tempDir);

    public static string Range(int first, int count) =>
        count <= 0 ? "" : count == 1 ? $"EP{first:D2}" : $"EP{first:D2}-EP{first + count - 1:D2}";

    // ── planning ─────────────────────────────────────────────────────────────

    public static async Task<DiscPlan> PlanAsync(Source source, Options o, string? makemkvcon)
    {
        var plan = new DiscPlan { Source = source };
        try
        {
            if (source.IsDvd) await PlanDvdAsync(plan, o, makemkvcon);
            else              await PlanMkvAsync(plan, o);
        }
        catch (Exception ex)
        {
            plan.Error = ex.Message;
        }
        return plan;
    }

    static async Task PlanMkvAsync(DiscPlan plan, Options o)
    {
        try   { plan.MkvChapters = await Ffmpeg.GetChaptersAsync(plan.Source.Path, o.Verbose); }
        catch (Exception ex) { plan.Error = $"ffprobe error: {ex.Message}"; return; }

        if (plan.MkvChapters.Count == 0)
        {
            plan.Error = "No chapters found in the file.";
            return;
        }

        plan.MkvGroups = EpisodePlanner.Groups(
            SplitMode.FixedDefault, null, plan.MkvChapters, o.ChaptersPerEp, o.ChaptersAuto, o.Episodes, out string reason);
        plan.Notes.Add($"{plan.MkvChapters.Count} chapters; {reason}");
        plan.ExpectedEpisodes = plan.MkvGroups.Count;
    }

    static async Task PlanDvdAsync(DiscPlan plan, Options o, string? makemkvcon)
    {
        Source source = plan.Source;

        if (o.TitleSpec == "auto" || o.ListTitles)
        {
            plan.Structure      = DvdStructure.TryRead(source.Path, o.Verbose, out string? structureError);
            plan.StructureError = structureError;
        }

        if (makemkvcon is null)
        {
            plan.Error = "makemkvcon not found. Install MakeMKV (https://www.makemkv.com) or pass --makemkv <path>.";
            return;
        }

        try   { plan.Titles = await MakeMkv.ScanAsync(makemkvcon, MakeMkv.SourceSpec(source.Path), o.MinLength, o.Verbose); }
        catch (Exception ex) { plan.Error = $"MakeMKV scan failed: {ex.Message}"; return; }

        if (plan.Titles.Count == 0)
        {
            plan.Error = "No titles found on the disc (try a smaller --min-length).";
            return;
        }

        switch (o.TitleSpec)
        {
            case "auto":
                plan.Jobs = EpisodePlanner.Auto(plan.Titles, plan.Structure, plan.Notes);
                break;

            case "longest":
                DvdTitle longest = EpisodePlanner.Longest(plan.Titles);
                plan.Notes.Add($"longest title {longest.Id}; episodes are cut from its chapters");
                plan.Jobs = [new TitleJob(longest, SplitMode.AutoChapters, null)];
                break;

            default:
                if (!TryResolveTitles(o.TitleSpec, plan.Titles, out List<DvdTitle> selected, out string? selectionError))
                {
                    plan.Error = selectionError;
                    return;
                }
                bool multi = selected.Count > 1;
                plan.Notes.Add(multi
                    ? $"titles {string.Join(", ", selected.Select(t => t.Id))}{(o.ChapterOverride ? "" : "; one episode per title")}"
                    : $"title {selected[0].Id}{(o.ChapterOverride ? "" : "; episodes are cut from its chapters")}");
                plan.Jobs = selected.Select(t => new TitleJob(t, multi ? SplitMode.WholeTitle : SplitMode.AutoChapters, null)).ToList();
                break;
        }

        if (o.ChaptersPerEp is int n) plan.Notes.Add($"{n} chapters per episode (--chapters-per-ep)");
        else if (o.ChaptersAuto)      plan.Notes.Add("chapter pattern detected after ripping (--chapters-per-ep auto)");
        else if (o.Episodes is int e) plan.Notes.Add($"{e} episodes per title (--episodes)");

        plan.ExpectedEpisodes = plan.Jobs.Sum(job => ExpectedEpisodes(job, o));
        plan.Estimated        = o.ChaptersAuto
                             || (o.ChaptersPerEp is null && o.Episodes is null && plan.Jobs.Any(j => j.Mode == SplitMode.AutoChapters));
    }

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

    /// <summary>How many episode numbers a job will use, so numbering stays stable across sources and failures.</summary>
    static int ExpectedEpisodes(TitleJob job, Options o)
    {
        if (o.ChaptersPerEp is int n) return Math.Max(1, (int)Math.Ceiling(job.Title.Chapters / (double)n));
        if (o.Episodes is int e)      return e;
        return job.Mode switch
        {
            SplitMode.WholeTitle    => 1,
            SplitMode.ChapterRanges => job.Ranges?.Count ?? 1,
            _                       => EpisodePlanner.EstimateEpisodes(job.Title.Duration, job.Title.Chapters),
        };
    }

    /// <summary>Gives every plan its first episode number, in order; failed plans use none.</summary>
    public static void AssignNumbers(List<DiscPlan> plans, int startEp)
    {
        int ep = startEp;
        foreach (DiscPlan plan in plans)
        {
            plan.FirstEpisode = ep;
            if (plan.Error is null) ep += plan.ExpectedEpisodes;
        }
    }

    // ── printing ─────────────────────────────────────────────────────────────

    /// <summary>One line per source: name, episode numbers, and how the cuts were decided.</summary>
    public static void PrintOverview(List<DiscPlan> plans)
    {
        int width = plans.Max(p => p.Source.Name.Length);
        for (int i = 0; i < plans.Count; i++)
        {
            DiscPlan p    = plans[i];
            string   what = p.Error is not null
                ? $"SKIPPED: {p.Error}"
                : $"{(p.Estimated ? "about " : "")}{p.ExpectedEpisodes} episode{(p.ExpectedEpisodes == 1 ? "" : "s")}: {string.Join("; ", p.Notes)}";
            Console.WriteLine($"  {i + 1,3}. {p.Source.Name.PadRight(width)}  {p.EpisodeRange,-11}  {what}");
        }
    }

    /// <summary>Everything known about a source: disc tables, menu targets, MakeMKV titles, plan and chapter lengths.</summary>
    public static void PrintDetails(DiscPlan plan, Options o)
    {
        Source s = plan.Source;
        Console.WriteLine($"{s.Name} ({s.KindText}){(plan.EpisodeRange.Length > 0 ? $"  {plan.EpisodeRange}" : "")}");

        if (!s.IsDvd)
        {
            Console.WriteLine(plan.Error is not null ? $"  ERROR: {plan.Error}" : $"  {plan.Notes[0]}");
            Console.WriteLine();
            return;
        }

        if (plan.Structure is { } dvd)
        {
            Console.WriteLine($"  Disc:     {dvd.VolumeLabel}: {dvd.Titles.Count} title(s), {dvd.Screens.Count} menu screen(s), {dvd.ButtonCount} button target(s)");
            Console.WriteLine("            Title  Length    Ch  VTS  Cells  Angles");
            foreach (IfoTitle t in dvd.Titles)
                Console.WriteLine($"            {t.Number,5}  {t.Duration,-8:h\\:mm\\:ss}  {t.Chapters,2}  {t.Vts,3}  {t.Cells,5}  {t.Angles,6}");
            foreach (MenuScreen m in dvd.Screens)
                Console.WriteLine($"            {m.Source}: {string.Join(" ", m.Targets)}");
        }
        else
        {
            Console.WriteLine($"  Disc:     structure not readable ({plan.StructureError})");
        }

        if (plan.Titles.Count > 0)
        {
            Console.WriteLine($"  MakeMKV:  {plan.Titles.Count} title(s) at least {o.MinLength} s long");
            Console.WriteLine("            Id  Length    Ch  Size      Name");
            foreach (DvdTitle t in plan.Titles)
                Console.WriteLine($"            {t.Id,2}  {t.Duration,-8:h\\:mm\\:ss}  {t.Chapters,2}  {t.Size,-8}  {t.Name}");
        }

        if (plan.Error is not null)
        {
            Console.WriteLine($"  ERROR:    {plan.Error}");
        }
        else
        {
            Console.WriteLine($"  Plan:     {string.Join("; ", plan.Notes)}");
            foreach (TitleJob job in plan.Jobs)
            {
                string how = o.ChapterOverride ? "" : job.Mode switch
                {
                    SplitMode.WholeTitle    => ": one episode",
                    SplitMode.ChapterRanges => $": {job.Ranges!.Count} episodes starting at chapters {string.Join(", ", job.Ranges.Select(r => r.First))}",
                    _                       => ": chapters grouped after ripping",
                };
                Console.WriteLine($"            rip title {job.Title.Id} ({job.Title.Duration:h\\:mm\\:ss}, {job.Title.Chapters} ch){how}");
                if (plan.Structure is not null && EpisodePlanner.IfoFor(job.Title, plan.Structure) is { ChapterLengths.Count: > 0 } ifo)
                    Console.WriteLine($"              chapters: {DescribeChapters(ifo.ChapterLengths, job.Mode == SplitMode.ChapterRanges ? job.Ranges : null)}");
            }
        }
        Console.WriteLine();
    }

    /// <summary>Chapter lengths as "m:ss", with " | " where the planned episodes begin.</summary>
    static string DescribeChapters(List<TimeSpan> lengths, List<(int First, int Last)>? ranges)
    {
        var starts = ranges is null ? [] : ranges.Skip(1).Select(r => r.First - 1).ToHashSet();
        return string.Join(" ", lengths.Select((t, i) => (starts.Contains(i) ? "| " : "") + EpisodePlanner.Fmt(t.TotalSeconds)));
    }

    public static string DescribeMode(Options o, string encoder)
    {
        if (!o.Encode) return "stream copy";
        string label = encoder == "h264_nvenc" ? "NVENC" : "CPU (libx264)";
        return $"H.264/{label} (CQ {o.Cq}){(o.Deinterlace ? " + yadif" : "")}{(o.KeepMkv ? ", keeping MakeMKV output" : "")}";
    }

    // ── running ──────────────────────────────────────────────────────────────

    public static async Task<(int Done, int Failed)> RunAsync(List<DiscPlan> plans, Options o, ShowInfo? show, string encoder)
    {
        var total    = Stopwatch.StartNew();
        var problems = new List<string>();
        int done = 0, failed = 0, ep = o.StartEp;

        for (int i = 0; i < plans.Count; i++)
        {
            DiscPlan plan  = plans[i];
            string   label = plans.Count > 1 ? $"Disc {i + 1}/{plans.Count}: " : "";

            if (plan.Error is not null)
            {
                Console.WriteLine($"=== {label}{plan.Source.Name}: skipped ({plan.Error})");
                Console.WriteLine();
                problems.Add($"{plan.Source.Name}: {plan.Error}");
                failed++;
                continue;
            }

            Console.WriteLine($"=== {label}{plan.Source.Name}  ({Range(ep, plan.ExpectedEpisodes)}) ===");
            var clock = Stopwatch.StartNew();
            var (d, f, consumed) = plan.Source.IsDvd
                ? await RunDvdAsync(plan, ep, o, show, encoder, problems)
                : await RunMkvAsync(plan, ep, o, show, encoder, problems);
            done   += d;
            failed += f;
            ep     += consumed;
            Console.WriteLine($"    {d} episode(s) done{(f > 0 ? $", {f} failed" : "")} in {Ui.Time(clock.Elapsed)}");
            Console.WriteLine();
        }

        Console.WriteLine($"Finished in {Ui.Time(total.Elapsed)}: {plans.Count} source(s), {done} episode(s) extracted, {failed} failed.");
        foreach (string problem in problems) Console.Error.WriteLine($"  {problem}");
        return (done, failed);
    }

    static async Task<(int Done, int Failed, int Consumed)> RunMkvAsync(
        DiscPlan plan, int epStart, Options o, ShowInfo? show, string encoder, List<string> problems)
    {
        var (done, failed) = await SplitAsync(plan.Source.Path, plan.MkvChapters, plan.MkvGroups, epStart,
                                              plan.Source.OutputDir, BaseName(plan.Source, o, show), show, o, encoder, problems);
        return (done, failed, plan.MkvGroups.Count);
    }

    static async Task<(int Done, int Failed, int Consumed)> RunDvdAsync(
        DiscPlan plan, int epStart, Options o, ShowInfo? show, string encoder, List<string> problems)
    {
        Source source     = plan.Source;
        string makemkvcon = MakeMkv.Locate(o.MakeMkvPath)!;   // planning already checked it exists
        string spec       = MakeMkv.SourceSpec(source.Path);
        string baseName   = BaseName(source, o, show);
        int    ep = epStart, done = 0, failed = 0;

        tempDir = Path.Combine(source.OutputDir, $"{source.Stem}.makemkv-tmp");
        try
        {
            for (int i = 0; i < plan.Jobs.Count; i++)
            {
                TitleJob job    = plan.Jobs[i];
                DvdTitle title  = job.Title;
                string   prefix = $"  [{i + 1}/{plan.Jobs.Count}] Ripping title {title.Id} ({title.Duration:h\\:mm\\:ss}, {title.Chapters} ch)";
                var      clock  = Stopwatch.StartNew();
                if (o.Verbose) Console.WriteLine($"{prefix} ...");

                string mkv;
                try
                {
                    Action<double>? onProgress = o.Verbose ? null : pct => Ui.Bar(prefix, pct);
                    mkv = await MakeMkv.RipTitleAsync(makemkvcon, spec, title, o.MinLength, tempDir, o.Verbose, onProgress);
                    Ui.End($"{prefix} done  {Ui.Time(clock.Elapsed)}  {Ui.Size(new FileInfo(mkv).Length)}");
                }
                catch (Exception ex)
                {
                    Ui.End($"{prefix} FAILED");
                    Console.Error.WriteLine($"    -> {ex.Message}");
                    problems.Add($"{source.Name}, title {title.Id}: {ex.Message}");
                    failed++;
                    ep += ExpectedEpisodes(job, o);   // keep numbering stable
                    continue;
                }

                List<ChapterInfo> chapters;
                try
                {
                    chapters = await Ffmpeg.GetChaptersAsync(mkv, o.Verbose);
                    if (chapters.Count == 0)   // no chapter markers: the whole title is one episode
                        chapters = [new ChapterInfo(0, await Ffmpeg.GetDurationAsync(mkv, o.Verbose), title.Name)];
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    -> ffprobe error: {ex.Message}");
                    problems.Add($"{source.Name}, title {title.Id}: ffprobe error: {ex.Message}");
                    failed++;
                    ep += ExpectedEpisodes(job, o);
                    continue;
                }

                var groups = EpisodePlanner.Groups(job.Mode, job.Ranges, chapters, o.ChaptersPerEp, o.ChaptersAuto, o.Episodes, out string reason);
                Console.WriteLine($"        {chapters.Count} chapter(s), {groups.Count} episode(s): {reason}");

                var (d, f) = await SplitAsync(mkv, chapters, groups, ep, source.OutputDir, baseName, show, o, encoder, problems);
                done   += d;
                failed += f;
                ep     += d + f;

                if (o.KeepMkv)
                {
                    string kept = Path.Combine(source.OutputDir, plan.Jobs.Count > 1 ? $"{source.Stem} - Title{title.Id:D2}.mkv" : $"{source.Stem}.mkv");
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
            tempDir = null;
        }

        return (done, failed, ep - epStart);
    }

    /// <summary>Extracts one episode per chapter group; returns how many succeeded and failed.</summary>
    static async Task<(int Done, int Failed)> SplitAsync(
        string inputFile, List<ChapterInfo> chapters, List<(int First, int Last)> groups, int epStart,
        string outputDir, string baseName, ShowInfo? show, Options o, string encoder, List<string> problems)
    {
        int failed = 0;

        for (int i = 0; i < groups.Count; i++)
        {
            int epNum = epStart + i;
            var (chFirst, chLast) = groups[i];

            var span = new ChapterInfo(chapters[chFirst].Start, chapters[chLast].End, chapters[chFirst].Title);

            string outputFile = Path.Combine(outputDir, FileName(baseName, epNum, show));
            string label      = chFirst == chLast ? $"ch {chFirst + 1}" : $"ch {chFirst + 1}-{chLast + 1}";
            string prefix     = $"  [{i + 1}/{groups.Count}] EP{epNum:D2} ({label})";
            var    clock      = Stopwatch.StartNew();
            if (o.Verbose) Console.WriteLine($"{prefix} -> {Path.GetFileName(outputFile)}");

            try
            {
                Action<double>? onProgress = o.Verbose ? null : pct => Ui.Bar(prefix, pct);
                await Ffmpeg.ExtractAsync(inputFile, span, outputFile, o, encoder, onProgress);
                Ui.End($"{prefix} done  {Ui.Time(clock.Elapsed)}  {Ui.Size(new FileInfo(outputFile).Length)}  {Path.GetFileName(outputFile)}");
            }
            catch (Exception ex)
            {
                Ui.End($"{prefix} FAILED");
                Console.Error.WriteLine($"    -> {ex.Message}");
                problems.Add($"{Path.GetFileName(outputFile)}: {ex.Message}");
                failed++;
            }
        }

        return (groups.Count - failed, failed);
    }

    // ── naming ───────────────────────────────────────────────────────────────

    public static string BaseName(Source source, Options o, ShowInfo? show) =>
        show is not null   ? ShowLookup.SafeFileName(show.Name) :
        o.Show is not null ? ShowLookup.SafeFileName(o.Show)    :
                             source.Stem;

    public static string FileName(string baseName, int epNum, ShowInfo? show)
    {
        string? title = show?.TitleOf(epNum);
        return title is null
            ? $"{baseName} - EP{epNum:D2}.mkv"
            : $"{baseName} - EP{epNum:D2} - {ShowLookup.SafeFileName(title)}.mkv";
    }

    static void TryDeleteDirectory(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}

/// <summary>Console progress helpers: an in-place progress line that is replaced or completed.</summary>
static class Ui
{
    static int lastLength;

    /// <summary>Rewrites the current line without ending it.</summary>
    public static void Line(string text)
    {
        Console.Write("\r" + text.PadRight(lastLength));
        lastLength = text.Length;
    }

    /// <summary>Replaces the current line with its final text and ends it.</summary>
    public static void End(string text)
    {
        Line(text);
        Console.WriteLine();
        lastLength = 0;
    }

    /// <summary>Blanks the current progress line.</summary>
    public static void Clear()
    {
        if (lastLength == 0) return;
        Console.Write("\r" + new string(' ', lastLength) + "\r");
        lastLength = 0;
    }

    public static void Bar(string prefix, double pct)
    {
        const int width = 32;
        int filled = (int)(width * Math.Clamp(pct, 0, 1));
        Line($"{prefix} [{new string('#', filled)}{new string('.', width - filled)}] {pct * 100,3:F0}%");
    }

    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F0} MB" :
                            $"{bytes / 1024.0:F0} KB";
}
