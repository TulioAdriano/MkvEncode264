/// <summary>
/// Text-based front-end used when the program starts without inputs: asks for the source, the
/// show name, the first episode number and the video mode, then hands back the settings.
/// </summary>
static class Wizard
{
    public sealed record Setup(Options Options, List<Source> Sources, ShowInfo? Show);

    /// <summary>Returns null when the user cancels (EOF on input).</summary>
    public static async Task<Setup?> RunAsync(Options o)
    {
        Console.WriteLine("MkvEncode264 interactive setup. Press Enter to accept a [default]; Ctrl+C quits.");
        Console.WriteLine();

        // 1. what to process
        List<Source> sources;
        while (true)
        {
            string? path = Ask("Source: an ISO image, an MKV, a DVD folder, or a folder holding several ISOs", Directory.GetCurrentDirectory());
            if (path is null) return null;
            if (SourceDiscovery.TryDiscover([path.Trim().Trim('"')], out sources, out string? error))
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    Source s    = sources[i];
                    string size = s.Kind == SourceKind.DvdFolder ? "" : $", {Ui.Size(new FileInfo(s.Path).Length)}";
                    Console.WriteLine($"    {i + 1,3}. {s.Name}  ({s.KindText}{size})");
                }
                break;
            }
            Console.WriteLine($"    {error}");
        }

        // 2. naming
        ShowInfo? show = null;
        string? showName = Ask("Show name, for file names and episode titles from TVmaze (Enter for none)", o.Show ?? "");
        if (showName is null) return null;
        showName = showName.Trim();
        if (showName.Length > 0)
        {
            Ui.Line("    looking up on TVmaze...");
            show = await ShowLookup.FetchAsync(showName, o.Verbose, message => Ui.End($"    {message}"));
            if (show is not null)
            {
                string year = show.Premiered is { Length: >= 4 } p ? $" ({p[..4]})" : "";
                Ui.End($"    found: {show.Name}{year}, {show.Episodes.Count} episode titles");
                if (show.Episodes.Count > 0)
                    Console.WriteLine($"    first episodes: {string.Join(" / ", show.Episodes.Take(3))}");
            }
            o.Show = showName;
        }
        else
        {
            o.Show = null;
        }

        // 3. numbering
        int? startEp = AskInt("First episode number", o.StartEp, 1, int.MaxValue);
        if (startEp is null) return null;
        o.StartEp = startEp.Value;

        // 4. video
        int? mode = AskChoice("Video",
            ["Keep the original video (fast, lossless)", "Encode to H.264", "Encode to H.264 and deinterlace"],
            o.Deinterlace ? 3 : o.Encode ? 2 : 1);
        if (mode is null) return null;
        o.Encode      = mode >= 2;
        o.Deinterlace = mode == 3;
        if (o.Encode)
        {
            int? cq = AskInt("Quality 0-51 (lower is better)", o.Cq, 0, 51);
            if (cq is null) return null;
            o.Cq = cq.Value;
        }

        o.Inputs.Clear();
        o.Inputs.AddRange(sources.Select(s => s.Path));
        Console.WriteLine();
        return new Setup(o, sources, show);
    }

    public static bool Confirm(string question, bool defaultYes = true)
    {
        Console.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        string? answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is null) return false;
        return answer.Length == 0 ? defaultYes : answer is "y" or "yes";
    }

    static string? Ask(string label, string defaultValue)
    {
        Console.Write(defaultValue.Length > 0 ? $"{label} [{defaultValue}]: " : $"{label}: ");
        string? answer = Console.ReadLine();
        if (answer is null) return null;
        answer = answer.Trim();
        return answer.Length == 0 ? defaultValue : answer;
    }

    static int? AskInt(string label, int defaultValue, int min, int max)
    {
        while (true)
        {
            string? answer = Ask(label, defaultValue.ToString());
            if (answer is null) return null;
            if (int.TryParse(answer, out int value) && value >= min && value <= max) return value;
            Console.WriteLine(max == int.MaxValue ? $"    Please enter a number of at least {min}." : $"    Please enter a number between {min} and {max}.");
        }
    }

    static int? AskChoice(string label, string[] choices, int defaultChoice)
    {
        Console.WriteLine($"{label}:");
        for (int i = 0; i < choices.Length; i++)
            Console.WriteLine($"    {i + 1}. {choices[i]}");
        return AskInt("Choice", defaultChoice, 1, choices.Length);
    }
}
