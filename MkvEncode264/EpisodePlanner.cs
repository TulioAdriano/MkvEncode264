/// <summary>How the chapters of one ripped title are turned into episodes.</summary>
enum SplitMode
{
    /// <summary>The whole title is one episode.</summary>
    WholeTitle,
    /// <summary>Cut at chapter boundaries planned from the disc (<see cref="TitleJob.Ranges"/>).</summary>
    ChapterRanges,
    /// <summary>Find the episode boundaries from the chapter lengths after ripping.</summary>
    AutoChapters,
    /// <summary>Fixed groups of the default size unless the user asked otherwise (MKV input).</summary>
    FixedDefault,
}

/// <summary>A title to rip and how to cut it. <see cref="Ranges"/> are 1-based inclusive chapter spans.</summary>
record TitleJob(DvdTitle Title, SplitMode Mode, List<(int First, int Last)>? Ranges);

/// <summary>
/// Decides which titles of a disc are episodes and where each episode starts: from the menu
/// button targets when available, otherwise from title lengths, otherwise from the chapter
/// lengths of the longest title.
/// </summary>
static class EpisodePlanner
{
    public const int DefaultChaptersPerEpisode = 4;

    static readonly TimeSpan MinEpisode = TimeSpan.FromMinutes(3);   // anything shorter is an extra

    const double ShortestPlausible = 8 * 60;    // episode lengths considered when reading chapter patterns
    const double LongestPlausible  = 70 * 60;
    const double TypicalEpisode    = 24 * 60;   // preferred when a pattern is ambiguous
    const double MaxLeftover       = 5 * 60;    // extra chapters (previews, warnings) tolerated around a pattern

    // ── which titles ─────────────────────────────────────────────────────────

    public static List<TitleJob> Auto(List<DvdTitle> titles, DvdStructure? dvd, List<string> notes)
    {
        if (dvd is not null && FromMenus(titles, dvd, notes) is { } byMenu) return byMenu;
        if (FromDurations(titles, notes) is { } byLength) return byLength;

        DvdTitle longest = Longest(titles);
        string   why     = titles.Count == 1
            ? $"single title {longest.Id}"
            : $"no episode layout recognised; using the longest title {longest.Id}";

        // With the chapter lengths from the disc, the cuts can be planned before ripping.
        if (dvd is not null && IfoFor(longest, dvd) is { ChapterLengths.Count: > 1 } ifo
            && InferEpisodes(ifo.ChapterLengths.Select(t => t.TotalSeconds).ToList(), out var groups, out string reason))
        {
            notes.Add($"{why}; {reason}");
            return [new TitleJob(longest, SplitMode.ChapterRanges, groups.Select(g => (g.First + 1, g.Last + 1)).ToList())];
        }

        notes.Add($"{why}; chapters grouped after ripping");
        return [new TitleJob(longest, SplitMode.AutoChapters, null)];
    }

    public static DvdTitle Longest(List<DvdTitle> titles) =>
        titles.OrderByDescending(t => t.Duration).ThenByDescending(t => t.SizeBytes).First();

    /// <summary>The IFO entry that corresponds to a MakeMKV title (same chapter count and length).</summary>
    public static IfoTitle? IfoFor(DvdTitle title, DvdStructure dvd) =>
        dvd.Titles.FirstOrDefault(ifo => Matches(ifo, title));

    /// <summary>
    /// MakeMKV reports the DVD title number (attribute 24) for DVD sources, which is the reliable
    /// key. Without it, fall back to chapter count plus length; MakeMKV's length can differ from
    /// the IFO playback time by several seconds (it drops trailing stills and tiny cells).
    /// </summary>
    static bool Matches(IfoTitle ifo, DvdTitle mk)
    {
        if (mk.Chapters != ifo.Chapters) return false;
        if (mk.OriginalId is int number) return number == ifo.Number;
        double tolerance = Math.Max(15, 0.02 * ifo.Duration.TotalSeconds);
        return Math.Abs((mk.Duration - ifo.Duration).TotalSeconds) <= tolerance;
    }

    static DvdTitle? Match(IfoTitle ifo, List<DvdTitle> titles)
    {
        var candidates = titles.Where(t => Matches(ifo, t)).ToList();
        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(t => t.OriginalId == ifo.Number) ?? candidates[0];
    }

    /// <summary>Menu buttons: either one button per episode title, or buttons into chapters of one title.</summary>
    static List<TitleJob>? FromMenus(List<DvdTitle> titles, DvdStructure dvd, List<string> notes)
    {
        var byNumber = new Dictionary<int, DvdTitle>();
        foreach (IfoTitle ifo in dvd.Titles)
            if (Match(ifo, titles) is { } mk) byNumber[ifo.Number] = mk;
        if (byNumber.Count == 0 || dvd.Screens.Count == 0) return null;

        // (a) buttons that jump to whole titles: on each screen take the largest set of similar-length
        //     titles, which leaves out "play all" and extras that sit on the same screen
        var episodeTitles = new SortedSet<int>();
        foreach (MenuScreen screen in dvd.Screens)
        {
            var whole = screen.Targets.Where(t => t.Chapter is null).Select(t => t.Title).Distinct()
                .Where(n => byNumber.TryGetValue(n, out DvdTitle? mk) && mk.Duration >= MinEpisode)
                .ToList();
            var cluster = LargestSimilarCluster(whole, n => byNumber[n].Duration);
            if (cluster.Count >= 2) episodeTitles.UnionWith(cluster);
        }
        if (episodeTitles.Count >= 2)
        {
            var jobs = episodeTitles.Select(n => byNumber[n]).Distinct()
                .Select(mk => new TitleJob(mk, SplitMode.WholeTitle, null)).ToList();
            if (jobs.Count >= 2)
            {
                notes.Add($"menu buttons point to DVD titles {Join(episodeTitles)}; one episode per title");
                return jobs;
            }
        }

        // (b) buttons that jump to chapters inside one title. Scene-selection pages point to runs of
        //     consecutive chapters and are ignored; episode pages skip chapters.
        var starts = new Dictionary<int, SortedSet<int>>();
        foreach (MenuScreen screen in dvd.Screens)
            foreach (var g in screen.Targets.Where(t => t.Chapter is not null).GroupBy(t => t.Title))
            {
                var chapters = g.Select(t => t.Chapter!.Value).Distinct().Order().ToList();
                if (chapters.Count < 2) continue;
                if (chapters.Zip(chapters.Skip(1)).All(p => p.Second - p.First == 1)) continue;
                if (!starts.TryGetValue(g.Key, out SortedSet<int>? set)) starts[g.Key] = set = [];
                set.UnionWith(chapters);
            }

        foreach (var (number, set) in starts.Where(kv => byNumber.ContainsKey(kv.Key))
                                            .OrderByDescending(kv => byNumber[kv.Key].Duration))
        {
            DvdTitle mk = byNumber[number];
            set.Add(1);
            set.RemoveWhere(c => c > mk.Chapters);
            if (set.Count < 2) continue;

            var list   = set.ToList();
            var ranges = list.Select((c, i) => (c, i + 1 < list.Count ? list[i + 1] - 1 : mk.Chapters)).ToList();
            notes.Add($"menu buttons point to chapters {Join(list)} of DVD title {number}; episodes start there");
            return [new TitleJob(mk, SplitMode.ChapterRanges, ranges)];
        }

        return null;
    }

    /// <summary>Title lengths: several similar titles that add up to a longer "play all" title, or nothing but similar titles.</summary>
    static List<TitleJob>? FromDurations(List<DvdTitle> titles, List<string> notes)
    {
        var longOnes = titles.Where(t => t.Duration >= MinEpisode).OrderByDescending(t => t.Duration).ToList();
        if (longOnes.Count < 2) return null;

        DvdTitle longest = longOnes[0];
        var rest = LargestSimilarCluster(longOnes.Skip(1).ToList(), t => t.Duration);
        if (rest.Count >= 2)
        {
            double sum = rest.Sum(t => t.Duration.TotalSeconds);
            if (Math.Abs(sum - longest.Duration.TotalSeconds) <= 0.10 * longest.Duration.TotalSeconds)
            {
                var eps = rest.OrderBy(t => t.Id).ToList();
                notes.Add($"titles {Join(eps.Select(t => t.Id))} are similar in length and add up to title {longest.Id} (play all); one episode per title");
                return eps.Select(t => new TitleJob(t, SplitMode.WholeTitle, null)).ToList();
            }
        }

        var all = LargestSimilarCluster(longOnes, t => t.Duration);
        if (all.Count == longOnes.Count)
        {
            var eps = all.OrderBy(t => t.Id).ToList();
            notes.Add($"titles {Join(eps.Select(t => t.Id))} are all similar in length; one episode per title");
            return eps.Select(t => new TitleJob(t, SplitMode.WholeTitle, null)).ToList();
        }

        return null;
    }

    /// <summary>Largest group of items whose lengths lie within 25% of the shortest member.</summary>
    static List<T> LargestSimilarCluster<T>(List<T> items, Func<T, TimeSpan> length)
    {
        var sorted = items.OrderBy(length).ToList();
        var best   = new List<T>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var    cluster  = new List<T> { sorted[i] };
            double shortest = length(sorted[i]).TotalSeconds;
            for (int j = i + 1; j < sorted.Count && length(sorted[j]).TotalSeconds <= shortest * 1.25; j++)
                cluster.Add(sorted[j]);
            if (cluster.Count > best.Count) best = cluster;
        }
        return best;
    }

    // ── which chapters ───────────────────────────────────────────────────────

    /// <summary>
    /// Groups the chapters of a ripped file into episodes (0-based inclusive index spans).
    /// Explicit user settings win: --chapters-per-ep, then --episodes, then the job's own mode.
    /// </summary>
    public static List<(int First, int Last)> Groups(
        SplitMode mode, List<(int First, int Last)>? ranges, List<ChapterInfo> chapters,
        int? chaptersPerEp, bool chaptersAuto, int? episodes, out string reason)
    {
        int count = chapters.Count;

        if (chaptersPerEp is int n)
        {
            reason = $"{n} chapters per episode";
            return Fixed(count, n);
        }
        if (episodes is int e)
        {
            e      = Math.Clamp(e, 1, count);
            reason = $"{e} episode{(e == 1 ? "" : "s")} of {(count % e == 0 ? $"{count / e}" : "about " + Math.Round(count / (double)e, 1))} chapters";
            return Even(count, e);
        }
        if (mode == SplitMode.ChapterRanges && ranges is { Count: > 0 }
            && ranges[0].First == 1 && ranges[^1].First <= count && ranges[^1].Last >= count - 1)
        {
            reason = "episode boundaries planned from the disc";
            var planned = ranges.Select(r => (r.First - 1, Math.Min(r.Last, count) - 1)).ToList();
            planned[^1] = (planned[^1].Item1, count - 1);
            return planned;
        }
        if (mode == SplitMode.WholeTitle && !chaptersAuto)
        {
            reason = "one episode per title";
            return [(0, count - 1)];
        }
        if (chaptersAuto || mode is SplitMode.AutoChapters or SplitMode.ChapterRanges)
        {
            if (InferEpisodes(chapters.Select(c => c.End - c.Start).ToList(), out var groups, out reason))
                return groups;
            reason = $"no chapter pattern found; {DefaultChaptersPerEpisode} chapters per episode";
            return Fixed(count, DefaultChaptersPerEpisode);
        }

        reason = $"{DefaultChaptersPerEpisode} chapters per episode";
        return Fixed(count, DefaultChaptersPerEpisode);
    }

    public static List<(int First, int Last)> Fixed(int count, int perEpisode)
    {
        var groups = new List<(int, int)>();
        for (int first = 0; first < count; first += perEpisode)
            groups.Add((first, Math.Min(first + perEpisode, count) - 1));
        return groups;
    }

    static List<(int First, int Last)> Even(int count, int episodes)
    {
        var groups = new List<(int, int)>();
        int size = count / episodes, extra = count % episodes, first = 0;
        for (int i = 0; i < episodes; i++)
        {
            int last = first + size + (i < extra ? 1 : 0) - 1;
            groups.Add((first, last));
            first = last + 1;
        }
        return groups;
    }

    /// <summary>
    /// Finds episode boundaries in a run of chapter lengths (seconds). First looks for a repeating
    /// pattern (e.g. opening, part A, part B, ending every 4 chapters), tolerating a few short extra
    /// chapters at either end (previews, warnings). Failing that, cuts at the chapter boundaries
    /// closest to equal episode lengths. Returns 0-based inclusive index spans.
    /// </summary>
    public static bool InferEpisodes(List<double> lengths, out List<(int First, int Last)> groups, out string reason)
    {
        groups = [];
        reason = "";
        int n = lengths.Count;
        if (n < 2) return false;
        double total = lengths.Sum();

        // 1. repeating pattern
        (int P, int Start, int Episodes, int Band, double Score, double Distance)? best = null;
        for (int p = 1; p <= n; p++)
        {
            int episodes = n / p;
            int extra    = n % p;
            foreach (int start in extra == 0 ? [0] : new[] { 0, extra })
            {
                double blockTotal = 0;
                for (int i = start; i < start + episodes * p; i++) blockTotal += lengths[i];
                double epLen = blockTotal / episodes;
                if (epLen < ShortestPlausible || epLen > LongestPlausible) continue;
                if (total - blockTotal > MaxLeftover) continue;

                double score = Periodicity(lengths, start, p, episodes) + 0.05 * extra;
                if (score > 0.35) continue;

                int    band     = Band(epLen);
                double distance = Math.Abs(epLen - (band == 1 ? 45 * 60 : TypicalEpisode));
                score = Math.Round(score, 2);
                if (best is null || (band, score, distance).CompareTo((best.Value.Band, best.Value.Score, best.Value.Distance)) < 0)
                    best = (p, start, episodes, band, score, distance);
            }
        }
        if (best is { } b)
        {
            for (int k = 0; k < b.Episodes; k++)
                groups.Add((b.Start + k * b.P, b.Start + (k + 1) * b.P - 1));
            int extra = n - b.Episodes * b.P;
            if (b.Start > 0) groups[0]  = (0, groups[0].Last);                      // leading extras join the first episode
            else if (extra > 0) groups[^1] = (groups[^1].First, n - 1);            // trailing extras join the last one

            reason = b.Episodes == 1
                ? $"one episode of {Fmt(total)}"
                : $"chapter lengths repeat every {b.P} chapters: {b.Episodes} episodes of about {Fmt(total / b.Episodes)}"
                  + (extra > 0 ? $" ({extra} extra chapter{(extra == 1 ? "" : "s")} at the {(b.Start > 0 ? "start kept with the first" : "end kept with the last")} episode)" : "");
            return true;
        }

        // 2. equal lengths
        if (total <= 32 * 60)
        {
            groups.Add((0, n - 1));
            reason = $"one episode of {Fmt(total)}";
            return true;
        }
        int count = Math.Clamp((int)Math.Round(total / TypicalEpisode), 2, n);
        var cumulative = new double[n + 1];
        for (int i = 0; i < n; i++) cumulative[i + 1] = cumulative[i] + lengths[i];

        var starts = new List<int> { 0 };
        for (int k = 1; k < count; k++)
        {
            double target = k * total / count;
            int    bestIndex = -1;
            double bestDistance = double.MaxValue;
            for (int j = starts[^1] + 1; j <= n - (count - k); j++)        // leave room for the remaining episodes
            {
                double d = Math.Abs(cumulative[j] - target);
                if (d < bestDistance) { bestDistance = d; bestIndex = j; }
            }
            if (bestIndex < 0) return false;
            starts.Add(bestIndex);
        }
        for (int k = 0; k < starts.Count; k++)
            groups.Add((starts[k], k + 1 < starts.Count ? starts[k + 1] - 1 : n - 1));

        double shortest = groups.Min(g => cumulative[g.Last + 1] - cumulative[g.First]);
        double longest  = groups.Max(g => cumulative[g.Last + 1] - cumulative[g.First]);
        if (shortest <= 0 || longest / shortest > 1.35)
        {
            groups.Clear();
            return false;
        }
        reason = $"{count} episodes cut at the chapters closest to equal lengths (about {Fmt(total / count)} each)";
        return true;
    }

    /// <summary>Average relative spread of the chapters sitting at the same position in every episode (0 = perfect repeat).</summary>
    static double Periodicity(List<double> lengths, int start, int p, int episodes)
    {
        if (episodes <= 1) return 0;
        double score = 0;
        for (int j = 0; j < p; j++)
        {
            double mean = 0;
            for (int k = 0; k < episodes; k++) mean += lengths[start + j + k * p];
            mean /= episodes;

            double variance = 0;
            for (int k = 0; k < episodes; k++)
            {
                double d = lengths[start + j + k * p] - mean;
                variance += d * d;
            }
            variance /= episodes;
            score += mean > 0 ? Math.Sqrt(variance) / mean : 0;
        }
        return score / p;
    }

    static int Band(double seconds) =>
        seconds is >= 17 * 60 and <= 32 * 60 ? 0 :
        seconds is >= 38 * 60 and <= 65 * 60 ? 1 : 2;

    public static string Fmt(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    static string Join(IEnumerable<int> values) => string.Join(", ", values);
}
