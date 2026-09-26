# MkvEncode264

A .NET 10 command-line tool that splits a multi-episode MKV file (e.g. from MakeMKV) into
individual episode files, with optional H.264 re-encoding.

It can also start straight from **DVD ISO images** (or `VIDEO_TS` folders): each disc is ripped
with MakeMKV's console tool and cut into episodes in the same run. Point it at a folder full of
ISOs and it processes them one after another, numbering the episodes across discs. The episode
layout is read from the disc itself (menu buttons, title lengths, chapter pattern), and with
`--show` the files are named after the show with episode titles looked up online. Run it
without arguments for a short interactive setup.

> **Cross-platform** — runs on Windows, Linux, and macOS anywhere .NET 10 and FFmpeg are available.

## Prerequisites

### .NET 10 SDK

Download and install from [dot.net](https://dot.net).

### FFmpeg

FFmpeg (and `ffprobe`) must be available on your `PATH`.

#### Windows

The easiest way is [Chocolatey](https://chocolatey.org):

```powershell
choco install ffmpeg
```

Other options:

| Method | Command |
|--------|---------|
| **winget** | `winget install Gyan.FFmpeg` |
| **Scoop** | `scoop install ffmpeg` |
| **Manual** | Download from [ffmpeg.org/download](https://ffmpeg.org/download.html) and add the `bin\` folder to your `PATH` |

#### Linux

```bash
# Debian / Ubuntu
sudo apt install ffmpeg

# Fedora
sudo dnf install ffmpeg

# Arch
sudo pacman -S ffmpeg
```

#### macOS

```bash
brew install ffmpeg
```

Verify the install on any platform:

```bash
ffmpeg -version
ffprobe -version
```

### MakeMKV (optional, only for DVD input)

[MakeMKV](https://www.makemkv.com/) is needed only when the input is an ISO image or a DVD
folder. The tool drives MakeMKV's console program, `makemkvcon`, and looks for it in this order:

1. the path given with `--makemkv <path>`
2. `makemkvcon64` / `makemkvcon` on your `PATH`
3. the default install location:

| OS | Location |
|----|----------|
| **Windows** | `C:\Program Files (x86)\MakeMKV\makemkvcon64.exe` (also `C:\Program Files\MakeMKV\`) |
| **Linux** | `/usr/bin/makemkvcon`, `/usr/local/bin/makemkvcon` |
| **macOS** | `/Applications/MakeMKV.app/Contents/MacOS/makemkvcon` |

> MakeMKV beta builds stop working about 60 days after their release. If the tool reports
> *"This application version is too old"*, install the current version from
> [makemkv.com/download](https://www.makemkv.com/download/), or enter the current beta key
> (published on the [MakeMKV forum](https://forum.makemkv.com/forum/viewtopic.php?f=5&t=1053))
> with `makemkvcon reg <key>`. DVD ripping itself is free in MakeMKV.

### NVIDIA GPU (optional, for `--encode`)

Any NVENC-capable GPU (GTX 10-series or newer) with up-to-date drivers is supported on Windows
and Linux. If no NVENC-capable GPU is detected at runtime, the tool automatically falls back to
**libx264** (software encoding) — no flags needed.

> macOS does not support NVENC. The tool will always use libx264 there.

---

## Building

```bash
git clone https://github.com/TulioAdriano/MkvEncode264.git
cd MkvEncode264
dotnet build -c Release
```

To produce a self-contained single-file executable:

```bash
# Windows
dotnet publish -c Release -r win-x64   --self-contained true -p:PublishSingleFile=true

# Linux
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# macOS (Apple Silicon)
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
```

---

## Usage

```
MkvEncode264                                interactive setup
MkvEncode264 <input> [<input> ...] [options]
```

An input is an MKV file, a DVD ISO image, a folder containing `VIDEO_TS`, or a folder holding
several ISO images / DVD folders. Several sources are processed one after another and episode
numbers continue from one source to the next.

### Interactive setup

Run the program without arguments and it asks for everything it needs:

```
Source: an ISO image, an MKV, a DVD folder, or a folder holding several ISOs [C:\DVDs]: C:\DVDs\Hamtaro
      1. HAMUTARO_01.iso  (DVD image, 4.1 GB)
      2. HAMUTARO_02.iso  (DVD image, 4.0 GB)
Show name, for file names and episode titles from TVmaze (Enter for none): Hamtaro
    found: Hamtaro (2002), 107 episode titles
    first episodes: Hamtaro / The Ham-Ham Clubhouse / Calling all Ham-Hams!
First episode number [1]:
Video:
    1. Keep the original video (fast, lossless)
    2. Encode to H.264
    3. Encode to H.264 and deinterlace
Choice [1]: 3
Quality 0-51 (lower is better) [20]:
```

It then scans every disc, shows the plan and asks before ripping. Options given on the command
line (for example `--encode --deinterlace`) become the defaults of the questions. `--interactive`
forces the setup even when input is redirected.

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--start-ep <N>` | `1` | First episode number for output file names |
| `--chapters-per-ep <N\|auto>` | `4` (MKV), `auto` (DVD) | Chapters grouped into one episode; `auto` finds the repeating chapter pattern |
| `--episodes <N>` | | Cut each source into N episodes with an equal number of chapters each |
| `--show <name>` | | Name files after the show and add episode titles looked up on [TVmaze](https://www.tvmaze.com) |
| `--encode` | off | Re-encode video to H.264 (NVENC if available, libx264 otherwise) |
| `--cq <N>` | `20` | Encode quality, `0`–`51` (lower = better; maps to CQ for NVENC, CRF for libx264) |
| `--deinterlace` | off | Deinterlace video with `yadif` (implies `--encode`) |
| `--verbose` | off | Print live `ffprobe` / `ffmpeg` / `makemkvcon` output and disc-structure diagnostics |
| `--interactive` | off | Start the interactive setup even when input is redirected |
| `-h`, `--help` | | Show help |

### DVD options

These apply when the input is an `.iso` file or a folder containing `VIDEO_TS`.

| Option | Default | Description |
|--------|---------|-------------|
| `--list-titles` | | Show the disc structure, MakeMKV's titles and the planned episodes for every source, then exit |
| `--title <spec>` | `auto` | `auto`: find the episodes from the disc; `longest`: the longest title, cut by chapters; `all` or ids such as `0`, `1-3`, `0,2`: those titles, one episode each |
| `--keep-mkv` | off | Keep the intermediate MKV produced by MakeMKV next to the source |
| `--min-length <sec>` | `120` | Ignore titles shorter than this many seconds (MakeMKV's own default) |
| `--makemkv <path>` | auto | Path to `makemkvcon` if it is not on `PATH` or in the default folder |

### Output naming

Output files are placed next to the source file and named:

```
{original name} - EP{NN}.mkv
```

For example, processing `My Video Disc1.mkv` produces:

```
My Video Disc1 - EP01.mkv
My Video Disc1 - EP02.mkv
My Video Disc1 - EP03.mkv
My Video Disc1 - EP04.mkv
```

A DVD source works the same way: `My Video Disc1.iso` produces `My Video Disc1 - EP01.mkv` and
so on, next to the ISO. With `--keep-mkv`, the MakeMKV rip is kept as `My Video Disc1.mkv`
(one title) or `My Video Disc1 - Title{NN}.mkv` (several titles).

With `--show "Hamtaro"` the show's name from TVmaze replaces the original name and the episode
title is appended:

```
Hamtaro - EP17 - Hamtaro, the Super Sleuth!.mkv
Hamtaro - EP18 - The Slipper Chase (aka The Glass Slipper).mkv
```

Episode numbers are absolute (`--start-ep` counts across seasons, in TVmaze's order). Check the
first few names against the disc: if the release you own uses a different episode order, leave
`--show` out or adjust `--start-ep`. When the show is not found or there is no network, files
are still written, named after the show without episode titles.

---

## Examples

**Disc 1 — fast stream copy, episodes 1–4:**

```bash
MkvEncode264 "My Video Disc1.mkv"
```

**Disc 2 — stream copy, starting at EP05:**

```bash
MkvEncode264 "My Video Disc2.mkv" --start-ep 5
```

**Disc 2 — encode, starting at EP05 (NVENC used automatically if available):**

```bash
MkvEncode264 "My Video Disc2.mkv" --start-ep 5 --encode
```

**Higher quality encode (CQ/CRF 18) with deinterlacing:**

```bash
MkvEncode264 "My Video Disc2.mkv" --start-ep 5 --encode --cq 18 --deinterlace
```

**Non-standard disc with 2 chapters per episode:**

```bash
MkvEncode264 "My Video Disc5.mkv" --start-ep 17 --chapters-per-ep 2 --encode
```

**DVD ISO — see what is on the disc and how it would be cut:**

```bash
MkvEncode264 "HAMUTARO_01.iso" --list-titles
```

**A whole box set — every ISO in the folder, ripped, cut and named in one go:**

```bash
MkvEncode264 "D:\DVDs\Hamtaro" --show "Hamtaro" --encode --deinterlace
```

**The second box, continuing the numbering:**

```bash
MkvEncode264 "D:\DVDs\Hamtaro Box 2" --show "Hamtaro" --start-ep 27 --encode --deinterlace
```

**Overriding the detection — titles 1 to 4 as one episode each, skipping a "play all" title 0:**

```bash
MkvEncode264 "My Video Disc3.iso" --title 1-4 --start-ep 9
```

---

## How it works

Every source is scanned and planned first, then the plan is printed, then the sources are
processed one after another:

```
Sources:  2 DVD images
MakeMKV:  C:\Program Files (x86)\MakeMKV\makemkvcon64.exe
Mode:     H.264/NVENC (CQ 20) + yadif

Plan:
    1. HAMUTARO_01.iso  EP01-EP04    4 episodes: menu buttons point to chapters 1, 5, 9, 13 of DVD title 1; episodes start there
    2. HAMUTARO_02.iso  EP05-EP08    4 episodes: menu buttons point to chapters 1, 5, 9, 13 of DVD title 1; episodes start there

=== Disc 1/2: HAMUTARO_01.iso  (EP01-EP04) ===
  [1/1] Ripping title 0 (1:29:48, 17 ch) done  0:09  3.9 GB
        17 chapter(s), 4 episode(s): episode boundaries planned from the disc
  [1/4] EP01 (ch 1-4) done  0:35  481 MB  Hamtaro - EP01 - Hamtaro.mkv
  ...
Finished in 2:41: 2 source(s), 8 episode(s) extracted, 0 failed.
```

While a step runs, its line shows a live progress bar. A `~` before an episode range means the
count is an estimate that is settled once the disc is ripped (only when the disc structure
could not be read). A source that cannot be planned is skipped and reported at the end; the
exit code is 1 when anything failed.

### MKV sources

1. **Chapter discovery** — `ffprobe` reads the MKV's chapter metadata (start/end timestamps).
2. **Grouping** — chapters are grouped into episodes: fixed groups of `--chapters-per-ep`, an
   equal split with `--episodes`, or the repeating chapter pattern with `--chapters-per-ep auto`.
   The start of the first chapter and the end of the last form the episode's time span.
3. **Extraction** — `ffmpeg` seeks to the start time and copies (or re-encodes) for the episode
   duration. All streams are preserved: every audio track, subtitles, and attachments (`-map 0`).

### DVD sources

1. **Disc structure** — the tool reads the DVD's own tables (`VIDEO_TS.IFO`, `VTS_xx_0.IFO`)
   straight from the image, without MakeMKV: the title list with chapter counts and lengths, the
   length of every chapter, and the navigation commands behind the menu buttons (following links
   into the small routing program chains that discs use). `--verbose` prints what was found.
2. **Scan** — `makemkvcon info` lists the titles MakeMKV offers. Titles shorter than
   `--min-length` are not shown.
3. **Plan** — with `--title auto` (the default) the episodes are located from, in order:
   - **Menu buttons.** Buttons that jump to several titles of similar length mark one episode per
     title. Buttons that jump into chapters of one title, skipping chapters between them, mark
     the episode starts (for example chapters 1, 5, 9 and 13). Scene-selection pages, which point
     to consecutive chapters, are ignored.
   - **Title lengths.** Several titles of similar length that add up to a longer "play all"
     title are the episodes; so are titles that are all of similar length.
   - **Chapter lengths of the longest title.** A repeating pattern such as opening, part A,
     part B, ending gives the episode size, tolerating a short extra chapter at the start or end
     (warnings, previews). Failing that, the title is cut at the chapter boundaries closest to
     equal episode lengths, and as a last resort into groups of 4 chapters.

   `--list-titles` prints the evidence and the plan for every source, including every chapter
   length with the planned cuts marked, so you can check it before ripping. Override with
   `--title`, `--chapters-per-ep` or `--episodes` when a disc is unusual.
4. **Rip** — each planned title is written by `makemkvcon mkv` into a temporary folder next to
   the source (`{name}.makemkv-tmp`), with a progress bar. Chapters, all audio tracks and
   subtitles are carried over by MakeMKV. The MKV then goes through the chapter split above and
   is deleted afterwards (unless `--keep-mkv` is given). The temporary folder is removed when the
   run finishes or is interrupted with Ctrl+C.

Make sure the drive holding the ISOs has room for one full title (roughly the size of an ISO)
in addition to the episode files.

### Stream copy vs. encode

| Mode | Speed | Quality | Use when |
|------|-------|---------|----------|
| Stream copy (default) | Near-instant | Lossless | Source quality is already good |
| `--encode` with NVENC | Real-time+ on GPU | Lossy | GPU available; want smaller files or need to deinterlace |
| `--encode` with libx264 | Slower (CPU) | Lossy | No NVENC GPU; automatically selected as fallback |

### Encoder auto-detection

When `--encode` is used, the tool queries `ffmpeg -encoders` at startup. If `h264_nvenc` is
listed, NVENC is used. Otherwise it falls back to `libx264` silently with a console note:

```
Note: NVENC not available, falling back to libx264 (CPU encoding).
```

The `--cq` value maps directly to NVENC's `cq` and libx264's `crf` — both use the same `0`–`51`
scale so the same number gives comparable visual quality across both encoders.

### Deinterlacing

`--deinterlace` applies the `yadif` filter before encoding. The default mode (`mode=0`) outputs
one frame per input frame, which is correct for telecined film/anime sources (the most common
case for DVD content). It always implies `--encode`.

---

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE.txt).
