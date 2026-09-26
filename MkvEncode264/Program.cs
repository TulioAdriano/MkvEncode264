// MkvEncode264 – split MKVs, or DVDs ripped with MakeMKV, into episode files.
//
// Program.cs wires the pieces together: Options (command line), Tui (full-screen setup),
// SourceDiscovery (what to process), Runner (plan, rip, split, progress). See CLAUDE.md.

if (!Options.TryParse(args, out Options o, out string? argError))
{
    Console.Error.WriteLine(argError);
    Options.PrintHelp();
    return 1;
}
if (o.Help)
{
    Options.PrintHelp();
    return 0;
}

// ── what to process ───────────────────────────────────────────────────────────
bool interactive = o.Interactive || (o.Inputs.Count == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected);
if (o.Inputs.Count == 0 && !interactive)
{
    Options.PrintHelp();
    return 1;
}

List<Source>    sources;
ShowInfo?       show    = null;
List<DiscPlan>? plans   = null;
string          encoder = "h264_nvenc";

if (interactive)
{
    Tui.Setup? setup = await Tui.RunAsync(o);
    if (setup is null)
    {
        Console.WriteLine("Cancelled.");
        return 1;
    }
    (o, sources, show, plans, encoder) = (setup.Options, setup.Sources, setup.Show, setup.Plans, setup.Encoder);
    if (show is not null)
    {
        string year = show.Premiered is { Length: >= 4 } p ? $" ({p[..4]})" : "";
        Console.WriteLine($"Show:     {show.Name}{year}, {show.Episodes.Count} episode titles from TVmaze");
    }
}
else
{
    if (!SourceDiscovery.TryDiscover(o.Inputs, out sources, out string? sourceError))
    {
        Console.Error.WriteLine(sourceError);
        return 1;
    }
    if (o.Show is not null && !o.ListTitles)
    {
        show = await ShowLookup.FetchAsync(o.Show, o.Verbose, message => Console.WriteLine($"Note: {message}"));
        if (show is not null)
        {
            string year = show.Premiered is { Length: >= 4 } p ? $" ({p[..4]})" : "";
            Console.WriteLine($"Show:     {show.Name}{year}, {show.Episodes.Count} episode titles from TVmaze");
        }
    }
}

bool anyDvd = sources.Any(s => s.IsDvd);
if (o.AnyDvdOption && !anyDvd)
    Console.WriteLine("Note: DVD options (--title, --list-titles, --keep-mkv, --min-length, --makemkv) are ignored for MKV input.");

if (o.Deinterlace && !o.Encode)
{
    Console.WriteLine("Note: --deinterlace requires encoding; --encode enabled automatically.");
    o.Encode = true;
}

if (plans is null && o.Encode && !o.ListTitles)
{
    encoder = await Ffmpeg.DetectEncoderAsync();
    if (encoder != "h264_nvenc")
        Console.WriteLine($"Note: NVENC not available, falling back to {encoder} (CPU encoding).");
}

string? makemkvcon = anyDvd ? MakeMkv.Locate(o.MakeMkvPath) : null;
if (anyDvd && makemkvcon is null)
{
    Console.Error.WriteLine(o.MakeMkvPath is null
        ? "makemkvcon not found. Install MakeMKV (https://www.makemkv.com) or pass --makemkv <path to makemkvcon>."
        : $"makemkvcon not found at: {o.MakeMkvPath}");
    return 1;
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\nInterrupted, cleaning up...");
    ChildProcess.KillCurrent();
    Runner.CleanupTemp();
    Environment.Exit(130);
};

// ── plan every source before touching anything ────────────────────────────────
var kinds = sources.GroupBy(s => s.KindText).Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
Console.WriteLine($"Sources:  {string.Join(", ", kinds)}");
if (makemkvcon is not null) Console.WriteLine($"MakeMKV:  {makemkvcon}");
Console.WriteLine($"Mode:     {Runner.DescribeMode(o, encoder)}");
Console.WriteLine();

if (plans is null)
{
    plans = [];
    for (int i = 0; i < sources.Count; i++)
    {
        if (!o.Verbose) Ui.Line($"Scanning {i + 1}/{sources.Count}: {sources[i].Name}...");
        plans.Add(await Runner.PlanAsync(sources[i], o, makemkvcon));
    }
    Ui.Clear();
    Runner.AssignNumbers(plans, o.StartEp);
}

if (o.ListTitles)
{
    foreach (DiscPlan plan in plans) Runner.PrintDetails(plan, o);
    Console.WriteLine("Run again without --list-titles to rip, or pick titles with --title <id>, --title all, or --title longest.");
    return plans.Any(p => p.Error is not null) ? 1 : 0;
}

Console.WriteLine("Plan:");
Runner.PrintOverview(plans);
Console.WriteLine();

// ── rip and split, one source after another ───────────────────────────────────
var (done, failed) = await Runner.RunAsync(plans, o, show, encoder);
return failed > 0 ? 1 : 0;
