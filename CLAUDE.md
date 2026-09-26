# MkvEncode264 — Project Context

This file is a self-contained technical brief for AI coding assistants (or new contributors)
picking up work on this repository. It should provide enough context to understand the project
and start making changes without needing prior conversation history.

## What this project is

**MkvEncode264** is a .NET 10 command-line tool that splits multi-episode MKV files (typically
ripped from a DVD/Blu-ray via [MakeMKV](https://www.makemkv.com/)) into individual per-episode
MKV files, using the source file's embedded chapter markers as split points.

It can also take **DVD ISO images or `VIDEO_TS` folders**, singly or a whole folder of them
(a box set). For each disc it reads the disc's own structure (IFO tables and menu button
commands) to work out where the episodes are, drives MakeMKV's console tool (`makemkvcon`) to
rip the needed title(s) to a temporary MKV, and runs the same chapter split. Episode numbers
continue from one source to the next. With `--show <name>` the output files are named after the
show with episode titles fetched from TVmaze. Started without arguments it opens a full-screen,
keyboard-driven setup screen in the terminal (nmtui-style, drawn with `System.Console` only)
for source, show, first episode and video mode, then a plan screen with Start/Back.

**Origin story:** The author has DVDs of the anime *Rurouni Kenshin*, where each disc's MKV rip
contains 4 episodes back-to-back, with each episode spanning 4 chapters (16 chapters total per
disc). The tool groups chapters into episode-sized chunks and extracts each into its own file.
The DVD support was added for a *Hamtaro* box set (`HAMUTARO_01.iso`): one 90-minute title with
17 chapters, whose episode menu jumps to chapters 1, 5, 9 and 13.

Optionally, the tool can re-encode video to H.264 during extraction (using NVIDIA NVENC hardware
encoding when available, falling back to libx264 CPU encoding otherwise), and can deinterlace
video using the `yadif` FFmpeg filter.

## Tech stack

- **.NET 10**, C# 14, console app; `Program.cs` uses top-level statements and only wires the
  other files together (see "Repository files")
- **FFmpeg / FFprobe** — invoked as external processes via `System.Diagnostics.Process`
  (no FFmpeg NuGet wrapper is used; this was a deliberate choice — see "Design decisions" below)
- **MakeMKV `makemkvcon`** — also invoked as an external process, in its "robot" mode
  (`-r`), only for DVD input
- **TVmaze REST API** (`https://api.tvmaze.com`, no key) via `HttpClient`, only with `--show`
- No NuGet packages at all; `PublishAot=true` in the csproj, so keep the code AOT/trim friendly
  (`JsonNode`, `Regex` without `Compiled`, no reflection)
- Solution file: `MkvEncode264.slnx`
- Single project: `MkvEncode264/MkvEncode264.csproj`

## How it works (pipeline)

`Program.cs` runs these steps:

1. **Options** (`Options.TryParse`) — a hand-rolled loop over `args`; positional arguments are
   inputs, `-`-prefixed ones are options. No inputs on a real console, or `--interactive`
   → `Tui.RunAsync` shows the setup screen (fields pre-filled from the options), scans and
   plans the sources itself and returns `Tui.Setup` (options, sources, show, plans, encoder),
   so `Program.cs` skips its own lookup/planning. No inputs + redirected console → help, exit 1.
2. **Sources** (`SourceDiscovery.TryDiscover`) — each input becomes a `Source` (`Mkv`,
   `DvdImage` or `DvdFolder`); a folder that is not itself a DVD expands to the `*.iso` files
   and DVD folders inside it, sorted naturally ("Disc 2" before "Disc 10").
3. **Naming** — with `--show`, `ShowLookup.FetchAsync` resolves the show on TVmaze
   (`singlesearch/shows?q=`) and fetches `shows/{id}/episodes`, flattened by (season, number)
   into absolute order. The base name becomes the show's TVmaze name and each file gets
   ` - {episode title}` appended (sanitised for file systems). Failures only print a note.
4. **Encoder detection** (`Ffmpeg.DetectEncoderAsync`, only with `--encode`) — runs
   `ffmpeg -hide_banner -encoders` and picks `h264_nvenc` when listed, else `libx264`.
5. **Planning** (`Runner.PlanAsync` per source, all sources before anything is written) —
   produces a `DiscPlan`: for MKVs the chapter list and groups; for DVDs the disc structure,
   MakeMKV's titles, the `TitleJob`s and the reasoning (`Notes`). A planning failure is stored
   in `DiscPlan.Error` and the source is skipped later. `Runner.AssignNumbers` gives every plan
   its first episode number; counts for `AutoChapters` jobs are estimates
   (`EpisodePlanner.EstimateEpisodes`, shown with `~`/"about").
6. **Overview** (`Runner.PrintOverview`, or `Runner.PrintDetails` per source with
   `--list-titles`, which then exits). In interactive mode the plan screen's *Start* button was
   the confirmation; the overview is still printed so the scrolling run output has context.
7. **Run** (`Runner.RunAsync`) — sources one after another with a `=== Disc i/n: name (EPxx-EPyy) ===`
   banner each; MKV sources go straight to the split, DVD sources through rip → chapters →
   groups → split. Each finished step prints elapsed time, output size and file name. The
   summary line and the list of problems come last; exit code 1 if anything failed.

### Splitting one file (`Runner.SplitAsync` → `Ffmpeg.ExtractAsync`)

For each chapter group, `ffmpeg` is invoked with:
- `-ss <start> -i <input> -t <duration>` — fast seek + duration-limited read
- `-map 0` — preserves **all** streams (every audio track, subtitle track, attachments),
  not just the "best" one FFmpeg would auto-select
- `-c:v copy` (stream copy, default) OR encode via NVENC/libx264 params if `--encode` is set
- `-vf yadif` if `--deinterlace` is set (this option forces `--encode` on, since filters
  require re-encoding — you cannot apply a video filter with stream copy)
- `-c:a copy -c:s copy` — audio/subtitles are always stream-copied regardless of `--encode`

Progress: in non-verbose mode ffmpeg's stderr `time=HH:MM:SS.ss` lines drive `Ui.Bar`; MakeMKV's
`PRGV` lines do the same for rips. `Ui` keeps one in-place line (`\r`), padded to erase the
previous text, and `Ui.End` finalises it. `--verbose` streams raw tool output instead.

### Chapter grouping (`EpisodePlanner.Groups`)

Turns a chapter list into 0-based index spans. Precedence: `--chapters-per-ep N` (fixed
groups), then `--episodes N` (equal chapter counts, remainder to the first ones), then the
job's mode: ranges planned from the disc (validated against the ripped chapter count, last
range extended to the end), whole title, or `InferEpisodes` on the chapter lengths. MKV input
defaults to fixed groups of 4; `--chapters-per-ep auto` turns on the inference for MKV input.

### DVD pipeline (ISO / VIDEO_TS input)

1. **Disc structure** (`DvdStructure.TryRead`, only for `--title auto` or `--list-titles`) —
   reads the DVD-Video tables directly, no MakeMKV involved:
   - **File access**: a `VIDEO_TS` folder, or a minimal ISO 9660 reader (primary volume
     descriptor at sector 16, root directory record at offset 156, directory records with
     little-endian extent/size, names with `;1` stripped). UDF-only images are not supported and
     yield "structure not readable", after which the planner works from MakeMKV's list alone.
   - **Titles**: `VIDEO_TS.IFO` → `TT_SRPT` (sector pointer at 0xC4; 12-byte entries: VTS number
     at +6, VTS_TTN at +7). Per VTS, `VTS_xx_0.IFO` → `VTS_PTT_SRPT` (0xC8: chapter → (PGC,
     program)) and `VTS_PGCIT` (0xCC). The PGC header is 236 bytes: programs @2, cells @3,
     playback time @4 (BCD `dvd_time_t`, frame-rate bits select 25/29.97 fps), command table
     offset @228, program map @230, cell playback table @232 (24 bytes per cell, time at +4).
     Chapter lengths are summed from the cells of each chapter's programs.
   - **Menus**: `VMGM_PGCI_UT` (VIDEO_TS.IFO 0xC8) and `VTSM_PGCI_UT` (VTS IFO 0xD0) give the
     menu PGCs and their pre/post/cell commands per language unit. The menu VOBs
     (`VIDEO_TS.VOB`, `VTS_xx_0.VOB`) are scanned sector by sector for navigation packs: MPEG pack
     start `00 00 01 BA`, then private stream 2 (`00 00 01 BF`) with substream id 0 = PCI. In the
     PCI, highlight info starts at +96: `hli_ss` @+0 (low 2 bits non-zero = buttons present),
     `btn_ns` @+17, 36 button entries of 18 bytes from +46, each with an 8-byte navigation
     command at +10. Commands are decoded like libdvdnav: top 3 bits = type; type 1 with bit 4
     set = Jump (sub-op in byte 1 low nibble: 2 JumpTT, 3 JumpVTS_TT, 5 JumpVTS_PTT; title in
     byte 5 & 0x7F, chapter in bytes 2-3 & 0x3FF). Any type 1–6 command whose link sub-op is 4
     is a LinkPGCN (target in bytes 6-7 & 0x7FFF) and is followed into that menu PGC's commands
     (depth ≤ 3), collecting every jump found there — discs commonly route buttons through tiny
     PGCs whose pre-command does the real jump (the Hamtaro disc does exactly this, with
     `SetHL_BTNN + LinkPGCN` buttons). Distinct button-target sets become `MenuScreen`s.
2. **Locate `makemkvcon`** (`MakeMkv.Locate`) — `--makemkv <path>` override, then `PATH`
   (`makemkvcon64` / `makemkvcon`), then the default install folders per OS (Windows
   `%ProgramFiles(x86)%\MakeMKV` and `%ProgramFiles%\MakeMKV`; macOS
   `/Applications/MakeMKV.app/Contents/MacOS`; Linux `/usr/bin`, `/usr/local/bin`,
   `/opt/makemkv/bin`).
3. **Scan** (`MakeMkv.ScanAsync`) — runs
   `makemkvcon -r --noscan --cache=512 --messages=-stdout --progress=-same --minlength=<N> info iso:<path>`
   (or `file:<folder>`), parses `TCOUNT:` and `TINFO:<title>,<attr>,<code>,"<value>"` lines
   into `DvdTitle` records. Attribute ids used: 2 name (absent for DVDs; "Title N" is shown
   instead), 8 chapter count, 9 duration `H:MM:SS`, 10 size text, 11 size bytes, 16 source
   file, 24 original title id (the DVD title number, e.g. "01"), 27 output file name.
   MakeMKV titles are matched to IFO titles by chapter count plus the DVD title number, or by
   duration within max(15 s, 2%) when that id is missing (`EpisodePlanner.Matches`) — MakeMKV's
   duration can be ~10 s shorter than the IFO playback time.
4. **Plan** (`EpisodePlanner.Auto`, `--title auto`) — produces `TitleJob`s (title + `SplitMode`
   + optional 1-based chapter ranges), trying in order:
   - `FromMenus`: (a) per screen, the largest cluster of similar-length titles among whole-title
     targets (≥ 2) → one episode per title (this drops "play all" and extras on the same
     screen); (b) chapter targets grouped by title, skipping screens whose chapters are
     consecutive (scene selection); the remaining chapter numbers plus chapter 1 are the episode
     starts of the longest such title → `ChapterRanges`.
   - `FromDurations`: titles ≥ 3 min; the largest cluster (within 25% of its shortest member)
     among the non-longest titles whose sum is within 10% of the longest title → one episode
     each; else all long titles similar → one episode each.
   - Longest title: if its IFO chapter lengths are known, `InferEpisodes` plans the cuts now
     (`ChapterRanges`); otherwise `AutoChapters` defers to the ripped file's chapters.
   `--title longest` / a single id → `AutoChapters`; `all` / several ids → `WholeTitle` each.
5. **Rip** (`MakeMkv.RipTitleAsync`) — `makemkvcon ... mkv <source> <id> <tempDir>` where
   `tempDir` is `{outputDir}/{stem}.makemkv-tmp` next to the source. The produced file is found
   by the name MakeMKV announced (attribute 27) or, failing that, the largest `*.mkv` in the
   temp folder. Failure = non-zero exit, a "Failed to save title" / "Copy complete ... N failed"
   message, or no output file. Title ids are positions in the list produced with the same
   `--minlength`, so the same value is passed to `info` and `mkv`.
6. **Split** — the temp MKV goes through the chapter grouping and `SplitAsync`. A title without
   chapter markers becomes one episode (duration from `ffprobe -show_format`). A failed rip still
   consumes the episode numbers it would have used. The temp MKV is deleted, or moved next to
   the source with `--keep-mkv` (`{stem}.mkv` for one job, `{stem} - Title{NN}.mkv` for several).
7. **Cleanup** — the temp folder is deleted in a `finally`, and a `Console.CancelKeyPress`
   handler kills the running child process (`ChildProcess.Current`) and deletes the temp folder
   before exiting with code 130.

### Chapter-length inference (`EpisodePlanner.InferEpisodes`)

Input: chapter lengths in seconds (from the IFO cells before ripping, or from ffprobe after).

1. **Repeating pattern.** For every period `p` and every alignment that leaves the remainder
   chapters at the start or the end: episode length must be 8–70 min, leftover chapters ≤ 5 min
   in total, and the periodicity score (mean relative standard deviation of the chapters sitting
   at the same position in every episode, plus 0.05 per leftover chapter) ≤ 0.35. Candidates are
   ranked by band (17–32 min first, then 38–65 min), then score (2 decimals), then distance from
   24 min (45 min in the second band). Leftover chapters join the adjacent episode.
2. **Equal lengths.** ≤ 32 min total → one episode. Otherwise N = round(total / 24 min), cut at
   the chapter boundaries closest to k·total/N; accepted only if the longest episode is ≤ 1.35×
   the shortest.
3. Otherwise the caller falls back to fixed groups of 4.

`EstimateEpisodes` (preview counts) mirrors rule 2. The constants live at the top of
`EpisodePlanner`.

## CLI reference

```
MkvEncode264                                interactive setup
MkvEncode264 <input> [<input> ...] [options]
```

| Option | Default | Description |
|---|---|---|
| `--start-ep <N>` | `1` | First episode number used in output file names (absolute numbering, continues across sources) |
| `--chapters-per-ep <N\|auto>` | `4` for MKV, `auto` for DVD titles | Fixed chapter groups, or infer the pattern |
| `--episodes <N>` | | Split each source into N episodes with equal chapter counts (remainder to the first ones) |
| `--show <name>` | | Base name + episode titles from TVmaze |
| `--encode` | off | Re-encode video to H.264 (NVENC if available, else libx264) |
| `--cq <N>` | `20` | Encode quality 0–51 (lower = better). Maps to NVENC's `-cq:v` and libx264's `-crf` — same scale, comparable quality |
| `--deinterlace` | off | Apply `yadif` deinterlace filter; implies `--encode` |
| `--verbose` | off | Stream raw ffprobe/ffmpeg/makemkvcon output; also prints disc-structure diagnostics (file list, nav packs, raw button commands) |
| `--interactive` | off | Run the setup even when input is redirected (used for scripted tests) |
| `-h`, `--help` | | Print usage help |

DVD-only options (ignored with a note when no source is a DVD):

| Option | Default | Description |
|---|---|---|
| `--list-titles` | | Print disc structure, MakeMKV titles, plan and chapter lengths for every source; exit |
| `--title <spec>` | `auto` | `auto`, `longest`, `all`, or ids/ranges like `0`, `1-3`, `0,2` |
| `--keep-mkv` | off | Keep the intermediate MakeMKV MKV next to the source |
| `--min-length <sec>` | `120` | Passed to makemkvcon as `--minlength`; hides short titles |
| `--makemkv <path>` | auto | Explicit path to `makemkvcon` |

**Output naming:** `{base} - EP{NN}[ - {episode title}].mkv` next to each source (for a DVD
folder: next to the folder). `{base}` is the source name, or the show's name with `--show`.

## Design decisions (important context for future changes)

- **No FFmpeg wrapper NuGet package** (e.g. FFMpegCore, Xabe.FFmpeg) — these still shell out to
  the `ffmpeg` binary internally, so they were judged as adding an abstraction layer without
  removing the external process dependency. True in-process bindings (FFmpeg.AutoGen,
  Sdcb.FFmpeg) require shipping native FFmpeg DLLs and a much lower-level API, which was
  considered overkill for this tool. **Conclusion: keep shelling out to `ffmpeg`/`ffprobe`
  directly via `Process.Start`, assume they're present on `PATH`.**
- **MakeMKV over FFmpeg's `dvdvideo` demuxer for ISOs** — FFmpeg 7+ can read DVD images natively
  only when built with libdvdnav/libdvdread; the common Windows "essentials" build (the author's)
  lacks it, and MakeMKV also handles CSS and produces the exact MKVs the tool was designed
  around. So the DVD path shells out to `makemkvcon` in robot mode, the format MakeMKV documents
  for automation (https://www.makemkv.com/developers/usage.txt). No MakeMKV library/SDK exists.
- **Own DVD structure reader instead of libdvdread/DiscUtils** — the tables needed (title list,
  chapter lengths, menu commands) are a few hundred lines of fixed-offset parsing; menu button
  targets are not exposed by MakeMKV at all. Keeping it in-repo avoids native libraries and
  NuGet dependencies and keeps the AOT publish simple. Everything is best effort: any parse
  failure degrades to "structure not readable" and the duration heuristics take over.
- **Menu evidence beats heuristics** — the disc's episode menu is the authoritative statement of
  where episodes start; lengths and chapter patterns are only used when menus are absent or use
  GPRM-driven routing that a static reader cannot follow.
- **Plan everything first, then run** — a batch scans every source (a few seconds each) before
  the first rip so the overview, the numbering and any skipped sources are visible up front;
  in interactive mode this is also the confirmation point.
- **Text-based front-end instead of a GUI** — `Tui.cs` is a small self-contained widget set
  (`TextField`, `Radio`, `Button`, `ListBox`, `Label`, `Form`) drawn into a `Canvas` (char +
  style per cell) and painted by `Terminal` with `Console.SetCursorPosition` and colour runs; keys
  come from `Console.ReadKey(true)` with `TreatControlCAsInput`. No Terminal.Gui/Spectre
  dependency (AOT-safe, works over SSH). Box drawing is UTF-8 (`OutputEncoding` is set); radio
  marks are `(X)` so they survive any font; colours assume a dark terminal; no mouse. The whole
  frame is redrawn after every key (80×22 cells, negligible). It only asks the four things that
  change per run; everything else stays a command-line option. Headless testing: the
  `MKVENCODE264_TUI_KEYS` key script replaces `ReadKey` and `MKVENCODE264_TUI_DUMP=1` prints
  every frame as text instead of painting.
- **Robot-mode parsing is tolerant** — `MakeMkv.ParseFields` splits on commas outside double
  quotes and honours backslash escapes; unknown line kinds are ignored; success is judged by exit
  code plus presence of `TCOUNT`/`TINFO` (scan) or the output file (rip), not by message codes,
  except for the well-known MSG 5021 "version too old" which adds a hint to the error text.
- **Temp folder lives next to the source** — a full DVD title is several GB, so it is written to
  `{name}.makemkv-tmp` beside the ISO rather than `%TEMP%`, which is often on a small system
  drive. The folder is always removed (normal exit, failure, Ctrl+C).
- **TVmaze for names** — keyless, JSON, covers anime and live action; episode numbering is the
  flattened (season, number) order, which matches the tool's absolute `EP{NN}` scheme. A wrong
  or missing match never blocks the rip; `--show` is opt-in because the disc label (e.g.
  `HAMUTARO_1`) is too unreliable to search automatically.
- **`-map 0` is mandatory** — without it, FFmpeg only keeps a single "best" audio stream and
  drops secondary audio tracks/subtitles. This was a real bug found and fixed during development.
- **Deinterlace implies encode** — filters can't be applied during stream copy; `--deinterlace`
  silently turns on `--encode` with a console note if the user didn't pass `--encode` explicitly.
- **`--cq` shared between NVENC and libx264** — chosen because both `-cq:v` (NVENC) and `-crf`
  (libx264) use the same conceptual 0–51 scale, so a single flag can serve both encoders without
  the user needing to know which one is active.
- **Cross-platform by construction** — no Windows-specific APIs are used in the code. The
  project targets Windows, Linux, and macOS equally; only the FFmpeg/MakeMKV installation paths
  differ per OS. NVENC requires an NVIDIA GPU (Windows/Linux only); macOS always falls back to
  libx264.
- **Progress bar vs. verbose are mutually exclusive** — `--verbose` is meant for debugging
  FFmpeg/makemkvcon invocations directly (it also echoes the exact makemkvcon command line); the
  progress bar is the "normal" cosmetic UX.

## Testing notes

There is no test project. What was verified, and how:

- **Disc structure reader** on the real `HAMUTARO_01.iso`: 3 titles (1:29:58 / 17 chapters,
  1:12, 0:25), chapter lengths showing the opening / part A / part B / ending pattern plus a
  1-second final chapter, and 5 menu screens: the episode page (`T1:1 T1:5 T1:9 T1:13`) and four
  scene pages with consecutive chapters plus a play-all button.
- **Real end-to-end runs** with MakeMKV v2.0.0: single disc (stream copy in 20 s; NVENC + yadif
  in 2:17, four ~22:31 files of ~0.5 GB), and a two-disc batch made of two hard links of the
  same ISO (`ln` in Git Bash) in one folder: EP01–EP08 in 35 s, names from TVmaze continuing
  across discs. Output files carry 1–2 extra partial chapters at the cut points because stream
  copy starts at the preceding keyframe (pre-existing, cosmetic).
- **Planner, batch loop and wizard** with a stand-in `makemkvcon` (a `.cmd` shim over a
  PowerShell script, outside the repo in `C:\Temp\mkvtest\fake-makemkv`) that replays
  robot-mode output for `info`/`mkv`, honours `--minlength=`, and copies FFmpeg-generated
  chaptered samples into the destination folder. It presents different title lists depending on
  the source name: `FAKE_DISC` (64 s / 16 ch, 16 s / 4 ch, and a title that always fails),
  `SERIES` (96-min play-all + four 24-min episode titles + trailer) and `HAMUTARO` (mirrors the
  real disc, backed by a 98-minute 17-chapter sample). A batch folder mixing those plus a DVD
  folder exercised discovery, numbering and per-disc banners. The setup screen was driven
  headlessly, e.g. `MKVENCODE264_TUI_DUMP=1 MKVENCODE264_TUI_KEYS="tab hamtaro tab tab down
  down enter tab tab enter enter" MkvEncode264 <batch folder> --interactive ...`, checking the
  dumped frames (field notes, TVmaze match, radio state, scan status, plan list, Back, Esc).
- **Chapter inference** on generated MKVs with chapter patterns `[90, 600, 660, 120] × 4`, with
  and without a leading or trailing extra chapter (needs `-map_chapters 1` when remuxing a new
  chapter list with ffmpeg).
- **Naming** against the live TVmaze API (`--show hamtaro` → "Hamtaro (2002)", 107 titles).
- The expired v1.18.3 was used to confirm the "version too old" error path.
- On Windows `Process.Start` can run a `.cmd` directly with `UseShellExecute = false`.
- `dotnet publish` (Native AOT) needs `vswhere.exe` on `PATH`; prepend
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer` when it fails with MSB3073.

## Repository files

- `MkvEncode264/Program.cs` — entry point: options → wizard or discovery → planning →
  overview/listing → run (top-level statements, ~120 lines)
- `MkvEncode264/Options.cs` — `Options` record-like class, `TryParse`, help text
- `MkvEncode264/Tui.cs` — full-screen setup and plan screens: widgets, `Form`, `Canvas`,
  `Terminal`, key script parsing
- `MkvEncode264/Sources.cs` — `Source`/`SourceKind`, folder expansion, natural sort
- `MkvEncode264/Runner.cs` — `DiscPlan`, planning per source, numbering, overview/details
  printing, the batch loop, rip → split, naming, and the `Ui` progress helper
- `MkvEncode264/Ffmpeg.cs` — ffprobe/ffmpeg helpers, `ChapterInfo`, `ChildProcess` tracker
- `MkvEncode264/MakeMkv.cs` — `makemkvcon` locator, robot-mode runner/parser, `ScanAsync`,
  `RipTitleAsync`, `DvdTitle` record, `MakeMkvException`
- `MkvEncode264/DvdStructure.cs` — ISO 9660 / folder file access, IFO title and chapter-length
  parsing, menu PGC command tables, navigation-pack button scanning and command decoding;
  `IfoTitle`, `MenuTarget`, `MenuScreen` records
- `MkvEncode264/EpisodePlanner.cs` — `SplitMode`, `TitleJob`, title selection heuristics,
  chapter grouping, `InferEpisodes`, `EstimateEpisodes`
- `MkvEncode264/ShowLookup.cs` — TVmaze lookup, `ShowInfo`, file-name sanitising
- `MkvEncode264/MkvEncode264.csproj` — project file (.NET 10, `PublishAot`)
- `MkvEncode264.slnx` — solution file
- `README.md` — user-facing documentation (install FFmpeg/MakeMKV, build, usage, examples)
- `LICENSE.txt` — GNU GPL v3.0

## Known limitations / possible future work

- No automated tests exist yet (no test project in the solution).
- Episode counts in the plan overview are estimates when a disc's structure is unreadable
  (`~` prefix); actual numbering is settled disc by disc while running, so later discs can shift.
- The setup screen asks only for source, show, first episode and video mode, does not remember
  previous answers between runs, needs at least 80×22 characters, and has no mouse support or
  per-disc detail view (use `--list-titles` for the chapter lengths). Progress during the run is
  the scrolling console output, not a screen.
- Menu buttons that set a register and let a routing PGC branch on it are followed only as far
  as static jumps reach; discs that compute targets at runtime fall back to the length and
  chapter heuristics. UDF-only ISO images (no ISO 9660 part) cannot be inspected.
- The equal-length fallback assumes ~24-minute episodes; hour-long shows without a chapter
  pattern need `--episodes` or `--chapters-per-ep`.
- TVmaze numbering follows the listed version's broadcast order, which may differ from a
  regional DVD release; there is no `--season`/offset option beyond `--start-ep`.
- No support yet for custom output directories (always writes next to each source) or custom
  output filename templates; the MakeMKV temp folder is also always next to the source, so the
  source drive needs free space for one full title (a `--temp-dir` option would fix both).
- Physical drives (`makemkvcon` sources `disc:N` / `dev:...`) and Blu-ray discs are not handled;
  only ISO images and `VIDEO_TS` folders are.
- `yadif` mode is hardcoded to `mode=0` (one output frame per input frame); `mode=1` (bob,
  doubles frame rate) is not exposed as a flag yet.
- Encoder detection re-runs `ffmpeg -encoders` on every invocation; no caching across runs.
- MakeMKV beta builds expire ~60 days after release; the tool surfaces MakeMKV's "version too
  old" message with a hint but cannot fix it (update MakeMKV or `makemkvcon reg <key>`).
