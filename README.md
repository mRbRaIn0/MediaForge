# MediaForge

Windows desktop app for **downloading, trimming, editing, converting, and sorting** videos,
audio, and photos. A user interface for `yt-dlp` and `FFmpeg` — no command line needed.

![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-blue) ![.NET](https://img.shields.io/badge/.NET-10-purple) ![License](https://img.shields.io/badge/License-MIT-green)

[Deutsch](README.de.md)

## Features

| Area | What it does |
| --- | --- |
| **Gallery** | Browse folders, timeline, search, albums, tags, and favorites |
| **yt-dlp (Download)** | Download video and audio; queue jobs; select time ranges, MP3, quality, and format; use cookies; import URL lists |
| **Cut** | Trim videos without re-encoding |
| **Editor** | Add markers, name and tag segments, assemble clips, and export |
| **Convert** | Convert formats and repair broken videos |
| **Speed Zone** | Speed up or slow down sections |
| **Link** | Join multiple videos into one |
| **Sorter** | Sort files into destination folders with keyboard shortcuts, preview, undo, and backup |

## Installation

### 1. Download MediaForge

Download the latest **`MediaForge.exe`** from [Releases](https://github.com/mRbRaIn0/MediaForge/releases)
and put it in any folder (for example, `C:\Tools\MediaForge\`). No installer or separate .NET
installation is required — just run it.

> Windows SmartScreen may warn you on first launch because the executable is unsigned:
> select **More info → Run anyway**.

### 2. Get yt-dlp and FFmpeg (required)

MediaForge uses two free tools that are **not included**:

- **yt-dlp** for downloads: <https://github.com/yt-dlp/yt-dlp/releases> (`yt-dlp.exe`)
- **FFmpeg** for trimming, conversion, and previews: <https://www.gyan.dev/ffmpeg/builds/> or
  <https://github.com/BtbN/FFmpeg-Builds/releases> (requires `ffmpeg.exe`, `ffprobe.exe`, and `ffplay.exe`)

**Easiest option:** Open the **yt-dlp** section in MediaForge and click **yt-dlp Update** and
**FFmpeg Update**. Both tools will be downloaded automatically.

**Manual option:** Put the executables next to `MediaForge.exe` or set their paths in **Einstellungen**
(Settings). MediaForge searches in this order:

1. Path configured in Settings
2. Folder containing `MediaForge.exe` and the working directory
3. `Weiteres` or `ffmpeg\bin` subfolder
4. `%LocalAppData%\MediaForge\tools`
5. Windows `PATH`

### 3. Configure settings (optional)

Open **Einstellungen** (Settings) from the main menu:

- **Download folders:** By default, `Videos\MediaForge` in your user profile (created automatically),
  with an option to use a custom folder
- **Tool paths** for `yt-dlp.exe` and `ffmpeg.exe`
- **URL file** for bulk imports
- **Cookies:** Off by default; load them from a browser (Firefox, Chrome, Edge, or Brave, optionally
  with a profile) or a `cookies.txt` file for content that requires a login
- **Sorter:** Source folder, destinations, and keyboard shortcuts

## Usage

### Download

1. Paste a URL and choose the mode (video/MP3), quality, format, and optionally a time range.
2. Add the job to the queue and start it. Progress and logs appear below.

**Import a URL list:** The first click on *Import* creates a `urls.txt` template. Use one URL per line;
`#` starts a comment. You can optionally add a time range and/or `mp3`:

```text
https://www.youtube.com/watch?v=XXXXXXXXXXX
https://www.youtube.com/watch?v=YYYYYYYYYYY 00:00:10-00:02:30 mp3
```

### Cut, Editor, Convert, Speed Zone, Link

Open a file, select ranges in the player or timeline, and export. The Editor saves markers and
segments next to the video in `<video>.mediaforge.json` so they are available next time.

### Sorter

Choose a source folder and destinations, then sort files one at a time:

| Key | Action |
| --- | --- |
| `1`–`9`, `0`, `Q`–`P` | Move to the corresponding destination (custom shortcuts can be set in Settings) |
| `Space` | Play/pause video |
| `←` / `→` | Previous/next file |
| `S` | Skip |
| `Ctrl+Z` | Undo the last sort action |
| `F5` | Rescan the folder |

Files are moved by default (copying is optional); existing files are never overwritten.

## Where is data stored?

| Data | Location |
| --- | --- |
| Settings | `%AppData%\MediaForge\settings.json` |
| Sorter progress | `%AppData%\MediaForge\sorter\` |
| Downloaded tools | Next to the executable, or `%LocalAppData%\MediaForge\tools` |
| Downloads | `Videos\MediaForge` (configurable) |

To uninstall, delete the executable and the folders listed above.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# Develop / run
dotnet run --project .\MediaForge\MediaForge.csproj -c Release -p:Platform=x64

# Portable single-file executable at .\release\MediaForge.exe
dotnet publish .\MediaForge\MediaForge.csproj -p:PublishProfile=Portable

# UI smoke tests
dotnet run --project .\MediaForge.SmokeTests\MediaForge.SmokeTests.csproj -c Release
```

Built with C# / .NET 10, WPF, and MVVM, with no NuGet dependencies. See
[`MediaForge/README.md`](MediaForge/README.md) for architecture details (in German).

## Legal notice

MediaForge is a user interface. Only download content you are authorized to use, and follow the
terms of service of the relevant platform. yt-dlp and FFmpeg have their own licenses (Unlicense
and LGPL/GPL, respectively).

## License

[MIT](LICENSE) © 2026 mRbRaIn0
