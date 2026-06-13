# MkvRemux

**Smart audio remux for MKV files.**

MkvRemux is a Windows command-line tool that takes MKV files with lossless surround audio (DTS-HD MA, TrueHD, DTS:X, etc.) and produces a new `.remux.mkv` alongside the original — adding compatible AAC surround and AAC stereo tracks while leaving all original streams untouched. It is designed for batch processing entire media libraries and integrates with Plex, Emby, Jellyfin, and other media servers that benefit from having a pre-encoded stereo or AAC fallback track.

---

## Requirements

- Windows 10 or later
- [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) or later
- [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) and [ffprobe](https://www.gyan.dev/ffmpeg/builds/) — both must be on your `PATH` or placed in the same directory as MkvRemux

---

## Installation

1. Download the latest release from the [Releases](../../releases) page.
2. Extract the zip to any folder, e.g. `C:\Tools\MkvRemux\`.
3. Ensure `ffmpeg.exe` and `ffprobe.exe` are accessible. The easiest approach is to add their folder to your system `PATH`.
4. Optionally add `MkvRemux.exe` to your `PATH` as well for use from any directory.

---

## Usage

```
MkvRemux [options] <path>
```

`<path>` can be a single `.mkv` file or a directory. When a directory is given, MkvRemux processes all MKV files found within it.

### Options

| Flag | Description |
|---|---|
| `--recursive` | Scan subdirectories recursively |
| `--skip-existing` | Skip any file whose `.remux.mkv` output already exists and was fully processed |
| `--stereo <mode>` | Stereo downmix mode: `dolby` (default) or `default` (ffmpeg built-in matrix) |
| `--dry-run` | Print what would be done without running ffmpeg |

### Examples

**Process a single file:**
```
MkvRemux "C:\Movies\Dune (2021)\Dune.mkv"
```

**Batch process a folder recursively, skipping already-processed files:**
```
MkvRemux --recursive --skip-existing "J:\Movies"
```

**Preview what would happen without writing any files:**
```
MkvRemux --dry-run --recursive "J:\Movies"
```

---

## How Audio Remuxing Works

MkvRemux uses `ffprobe` to inspect each MKV, then builds an `ffmpeg` command to produce the output file. No video data is re-encoded — the video stream is always stream-copied.

### Track layout in the output file

Given a source with a lossless surround track (e.g. DTS-HD MA 7.1), the output will contain:

| Track | Content | Notes |
|---|---|---|
| 0 | Original lossless audio | Copied verbatim, set as default |
| 1 | AAC surround (same channel count) | Transcoded at high bitrate |
| 2 | AAC Stereo | Downmixed from the surround source |
| … | Any additional audio tracks | Copied verbatim |
| … | All subtitle tracks | Copied verbatim |

### Stereo downmix modes

The stereo track (Track 2) can be created two ways, controlled by `--stereo`:

**`dolby`** (default) — Uses ffmpeg's `pan` filter with a Dolby-spec downmix matrix. Center channel and surround channels are folded in at -3 dB (0.707) and rear channels at -6 dB (0.5). This closely matches what a Dolby decoder would produce and is the recommended mode for home theater content.

**`default`** — Lets ffmpeg apply its built-in downmix matrix automatically via `-ac 2`. Simpler, but less controlled.

### Bitrate selection

| Source channels | AAC surround bitrate | AAC stereo bitrate |
|---|---|---|
| 7.1 | 768 kbps | 256 kbps |
| 5.1 | 640 kbps | 256 kbps |
| Other | 448 kbps | 256 kbps |

### Skip logic

When `--skip-existing` is set, MkvRemux checks for the presence of a `MkvRemux.TitleDescriptor` tag written into the output file's metadata during a prior successful run. If that tag is absent (e.g. the file exists but ffmpeg was interrupted), the output is deleted and re-processed.

---

## Output File Naming

Output files are written alongside the source file with `.remux.mkv` appended before the extension:

```
Source:  Movie (2021) [2160p][DTS-HD MA 7.1][h265].mkv
Output:  Movie (2021) [2160p][DTS-HD MA 7.1][h265].remux.mkv
```

The source file is never modified.

---

## License

MIT License. See [LICENSE](LICENSE) for details.
