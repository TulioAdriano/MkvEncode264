# MkvEncode264

A .NET 10 command-line tool that splits a multi-episode MKV file (e.g. from MakeMKV) into
individual episode files, with optional H.264 re-encoding.

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
MkvEncode264 <input.mkv> [options]
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--start-ep <N>` | `1` | First episode number for output file names |
| `--chapters-per-ep <N>` | `4` | Number of MKV chapters to group into one episode |
| `--encode` | off | Re-encode video to H.264 (NVENC if available, libx264 otherwise) |
| `--cq <N>` | `20` | Encode quality, `0`–`51` (lower = better; maps to CQ for NVENC, CRF for libx264) |
| `--deinterlace` | off | Deinterlace video with `yadif` (implies `--encode`) |
| `--verbose` | off | Print live `ffprobe` / `ffmpeg` output |
| `-h`, `--help` | | Show help |

### Output naming

Output files are placed next to the source file and named:

```
{original name} - EP{NN}.mkv
```

For example, processing `Rurouni Kenshin Disc1.mkv` produces:

```
Rurouni Kenshin Disc1 - EP01.mkv
Rurouni Kenshin Disc1 - EP02.mkv
Rurouni Kenshin Disc1 - EP03.mkv
Rurouni Kenshin Disc1 - EP04.mkv
```

---

## Examples

**Disc 1 — fast stream copy, episodes 1–4:**

```bash
MkvEncode264 "Rurouni Kenshin Disc1.mkv"
```

**Disc 2 — stream copy, starting at EP05:**

```bash
MkvEncode264 "Rurouni Kenshin Disc2.mkv" --start-ep 5
```

**Disc 2 — encode, starting at EP05 (NVENC used automatically if available):**

```bash
MkvEncode264 "Rurouni Kenshin Disc2.mkv" --start-ep 5 --encode
```

**Higher quality encode (CQ/CRF 18) with deinterlacing:**

```bash
MkvEncode264 "Rurouni Kenshin Disc2.mkv" --start-ep 5 --encode --cq 18 --deinterlace
```

**Non-standard disc with 2 chapters per episode:**

```bash
MkvEncode264 "Rurouni Kenshin Disc5.mkv" --start-ep 17 --chapters-per-ep 2 --encode
```

---

## How it works

1. **Chapter discovery** — `ffprobe` reads the MKV's chapter metadata (start/end timestamps).
2. **Grouping** — chapters are grouped in sets of `--chapters-per-ep`. The start of the first
   chapter and the end of the last form the episode's time span.
3. **Extraction** — `ffmpeg` seeks to the start time and copies (or re-encodes) for the episode
   duration. All streams are preserved: every audio track, subtitles, and attachments (`-map 0`).
4. **Progress** — in normal mode a live progress bar is shown; `--verbose` streams raw FFmpeg
   output instead.

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
