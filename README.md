# MkvEncode264

A .NET 10 command-line tool that splits a multi-episode MKV file (e.g. from MakeMKV) into
individual episode files, with optional H.264 re-encoding.

It can also start straight from a **DVD ISO image** (or a `VIDEO_TS` folder): the disc is ripped
with MakeMKV's console tool and cut into episodes in the same run. The episode layout is read
from the disc itself (menu buttons, title lengths, chapter pattern), and with `--show` the files
are named after the show with episode titles looked up online.

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
MkvEncode264 <input.mkv | disc.iso | DVD folder> [options]
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--start-ep <N>` | `1` | First episode number for output file names |
| `--chapters-per-ep <N\|auto>` | `4` (MKV), `auto` (DVD) | Chapters grouped into one episode; `auto` finds the repeating chapter pattern |
| `--episodes <N>` | | Cut the source into N episodes with an equal number of chapters each |
| `--show <name>` | | Name files after the show and add episode titles looked up on [TVmaze](https://www.tvmaze.com) |
| `--encode` | off | Re-encode video to H.264 (NVENC if available, libx264 otherwise) |
| `--cq <N>` | `20` | Encode quality, `0`–`51` (lower = better; maps to CQ for NVENC, CRF for libx264) |
| `--deinterlace` | off | Deinterlace video with `yadif` (implies `--encode`) |
| `--verbose` | off | Print live `ffprobe` / `ffmpeg` / `makemkvcon` output and disc-structure diagnostics |
| `-h`, `--help` | | Show help |

### DVD options

These apply when the input is an `.iso` file or a folder containing `VIDEO_TS`.

| Option | Default | Description |
|--------|---------|-------------|
| `--list-titles` | | Show the disc structure, MakeMKV's titles and the planned episodes, then exit |
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

**DVD ISO — rip, cut and name the episodes automatically:**

```bash
MkvEncode264 "HAMUTARO_01.iso" --show "Hamtaro" --encode --deinterlace
```

**Second disc of the same show — episodes 5 onwards:**

```bash
MkvEncode264 "HAMUTARO_02.iso" --show "Hamtaro" --start-ep 5 --encode --deinterlace
```

**Overriding the detection — titles 1 to 4 as one episode each, skipping a "play all" title 0:**

```bash
MkvEncode264 "My Video Disc3.iso" --title 1-4 --start-ep 9
```

---

## How it works

1. **Chapter discovery** — `ffprobe` reads the MKV's chapter metadata (start/end timestamps).
2. **Grouping** — chapters are grouped into episodes: fixed groups of `--chapters-per-ep`, an
   equal split with `--episodes`, or the repeating chapter pattern with `--chapters-per-ep auto`.
   The start of the first chapter and the end of the last form the episode's time span.
3. **Extraction** — `ffmpeg` seeks to the start time and copies (or re-encodes) for the episode
   duration. All streams are preserved: every audio track, subtitles, and attachments (`-map 0`).
4. **Progress** — in normal mode a live progress bar is shown; `--verbose` streams raw FFmpeg
   output instead.

### DVD sources

When the input is an ISO or a DVD folder, three steps run before the chapter split:

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

   `--list-titles` prints the evidence and the plan, including every chapter length with the
   planned cuts marked, so you can check it before ripping. Override with `--title`,
   `--chapters-per-ep` or `--episodes` when a disc is unusual.
4. **Rip** — each planned title is written by `makemkvcon mkv` into a temporary folder next to
   the source (`{name}.makemkv-tmp`), with a progress bar. Chapters, all audio tracks and
   subtitles are carried over by MakeMKV. The MKV then goes through the chapter split above and
   is deleted afterwards (unless `--keep-mkv` is given). The temporary folder is removed when the
   run finishes or is interrupted with Ctrl+C.

Make sure the drive holding the ISO has room for one full title (roughly the size of the ISO)
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
