using System.Text.RegularExpressions;

/// <summary>Command-line settings. Positional arguments are inputs; everything else is an option.</summary>
sealed class Options
{
    public List<string> Inputs        { get; } = [];
    public int          StartEp       { get; set; } = 1;
    public bool         Encode        { get; set; }
    public int          Cq            { get; set; } = 20;
    public bool         Deinterlace   { get; set; }
    public bool         Verbose       { get; set; }
    public int?         ChaptersPerEp { get; set; }          // --chapters-per-ep <N>
    public bool         ChaptersAuto  { get; set; }          // --chapters-per-ep auto
    public int?         Episodes      { get; set; }          // --episodes <N>
    public string?      Show          { get; set; }          // --show <name>
    public string       TitleSpec     { get; set; } = "auto"; // DVD sources only
    public bool         ListTitles    { get; set; }          // DVD sources only
    public bool         KeepMkv       { get; set; }          // DVD sources only
    public int          MinLength     { get; set; } = 120;   // DVD sources only; seconds (MakeMKV's own default)
    public string?      MakeMkvPath   { get; set; }          // DVD sources only
    public bool         Interactive   { get; set; }          // --interactive: run the setup even with redirected input
    public bool         Help          { get; set; }

    /// <summary>True when the user fixed how chapters are grouped, overriding what the disc suggests.</summary>
    public bool ChapterOverride => ChaptersPerEp is not null || ChaptersAuto || Episodes is not null;

    public bool AnyDvdOption => ListTitles || KeepMkv || TitleSpec != "auto" || MinLength != 120 || MakeMkvPath is not null;

    public static bool TryParse(string[] args, out Options o, out string? error)
    {
        o     = new Options();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg      = args[i];
            bool   hasValue = i + 1 < args.Length;
            switch (arg)
            {
                case "-h" or "--help":
                    o.Help = true;
                    break;

                case "--start-ep" when hasValue:
                    if (!int.TryParse(args[++i], out int startEp) || startEp < 1)
                    { error = "--start-ep must be a positive integer."; return false; }
                    o.StartEp = startEp;
                    break;

                case "--encode":
                    o.Encode = true;
                    break;

                case "--chapters-per-ep" when hasValue:
                    string perEpText = args[++i];
                    if (perEpText.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    { o.ChaptersAuto = true; o.ChaptersPerEp = null; }
                    else if (int.TryParse(perEpText, out int perEp) && perEp >= 1)
                    { o.ChaptersPerEp = perEp; o.ChaptersAuto = false; }
                    else
                    { error = "--chapters-per-ep must be a positive integer or 'auto'."; return false; }
                    break;

                case "--episodes" when hasValue:
                    if (!int.TryParse(args[++i], out int episodes) || episodes < 1)
                    { error = "--episodes must be a positive integer."; return false; }
                    o.Episodes = episodes;
                    break;

                case "--show" when hasValue:
                    o.Show = args[++i].Trim();
                    if (o.Show.Length == 0) { error = "--show needs a show name."; return false; }
                    break;

                case "--cq" when hasValue:
                    if (!int.TryParse(args[++i], out int cq) || cq is < 0 or > 51)
                    { error = "--cq must be between 0 and 51."; return false; }
                    o.Cq = cq;
                    break;

                case "--verbose":
                    o.Verbose = true;
                    break;

                case "--deinterlace":
                    o.Deinterlace = true;
                    break;

                case "--title" when hasValue:
                    o.TitleSpec = args[++i].Trim().ToLowerInvariant();
                    if (!IsValidTitleSpec(o.TitleSpec))
                    { error = "--title must be 'auto', 'longest', 'all', or title ids such as 0, 1-3 or 0,2,5."; return false; }
                    break;

                case "--list-titles":
                    o.ListTitles = true;
                    break;

                case "--keep-mkv":
                    o.KeepMkv = true;
                    break;

                case "--min-length" when hasValue:
                    if (!int.TryParse(args[++i], out int minLength) || minLength < 0)
                    { error = "--min-length must be zero or a positive number of seconds."; return false; }
                    o.MinLength = minLength;
                    break;

                case "--makemkv" when hasValue:
                    o.MakeMkvPath = args[++i];
                    break;

                case "--interactive":
                    o.Interactive = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unknown option or missing value: {arg}";
                        return false;
                    }
                    o.Inputs.Add(arg);
                    break;
            }
        }
        return true;
    }

    public static bool IsValidTitleSpec(string spec) =>
        Regex.IsMatch(spec, @"^(auto|longest|all|\d+(-\d+)?(,\d+(-\d+)?)*)$");

    public static void PrintHelp() => Console.WriteLine("""
        MkvEncode264 – Split MKVs, or DVDs ripped with MakeMKV, into episode files.

        USAGE
          MkvEncode264                          interactive setup (asks for everything below)
          MkvEncode264 <input> [<input> ...] [options]

        INPUTS
          An MKV file, a DVD ISO image, a folder containing VIDEO_TS, or a folder holding
          several ISO images / DVD folders. Several sources are processed one after another
          and episode numbers continue from one to the next.

        OPTIONS
          --start-ep <N>         First episode number (default: 1)
          --chapters-per-ep <N>  Chapters grouped into one episode file (default: 4 for MKV input;
                                 'auto' detects the repeating chapter pattern, the default for DVD titles)
          --episodes <N>         Cut each source into N episodes with an equal number of chapters
          --show <name>          Name files after the show and add episode titles looked up on TVmaze
          --encode               Encode video to H.264 (NVENC, or libx264 when unavailable)
          --cq <N>               Encode quality 0-51 (default: 20; lower = better)
          --deinterlace          Deinterlace video using yadif (implies --encode)
          --verbose              Print ffprobe / ffmpeg / makemkvcon output and disc diagnostics
          --interactive          Start the interactive setup even when input is redirected
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
          MkvEncode264
          MkvEncode264 "RurouniKenshin_Disc1.mkv"
          MkvEncode264 "RurouniKenshin_Disc2.mkv" --start-ep 5 --encode --cq 18
          MkvEncode264 "Hamtaro_Disc1.iso" --list-titles
          MkvEncode264 "Hamtaro_Disc1.iso" --show "Hamtaro" --encode --deinterlace
          MkvEncode264 "D:\DVDs\Hamtaro" --show "Hamtaro" --encode --deinterlace
        """);
}
