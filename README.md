# MediaForge

Windows-Desktop-App zum **Herunterladen, Schneiden, Bearbeiten, Konvertieren und Sortieren** von Videos,
Audio und Fotos. Eine Oberfläche für `yt-dlp` und `FFmpeg` – ohne Kommandozeile.

![Plattform](https://img.shields.io/badge/Windows-10%20%7C%2011-blue) ![.NET](https://img.shields.io/badge/.NET-10-purple) ![Lizenz](https://img.shields.io/badge/Lizenz-MIT-green)

## Funktionen

| Bereich | Was er kann |
| --- | --- |
| **Galerie** | Ordner einlesen, Zeitleiste, Suche, Alben, Tags, Favoriten |
| **yt-dlp (Download)** | Videos & Audio herunterladen, Warteschlange, Zeitbereiche, MP3, Qualität/Format, Cookies, URL-Liste importieren |
| **Cut** | Videos verlustfrei schneiden |
| **Editor** | Marker setzen, Bereiche benennen/taggen, Clips zusammenstellen und exportieren |
| **Convert** | Formate umwandeln und defekte Videos reparieren |
| **Speed Zone** | Abschnitte schneller oder langsamer abspielen lassen |
| **Link** | Mehrere Videos zu einem zusammenfügen |
| **Sorter** | Dateien per Tastendruck in Zielordner einsortieren (mit Vorschau, Rückgängig, Backup) |

## Installation

### 1. MediaForge herunterladen
Unter [Releases](https://github.com/mRbRaIn0/MediaForge/releases) die aktuelle **`MediaForge.exe`**
herunterladen und in einen beliebigen Ordner legen (z. B. `C:\Tools\MediaForge\`).
Es ist **keine Installation und kein .NET** nötig – einfach starten.

> Windows SmartScreen kann beim ersten Start warnen, weil die Exe nicht signiert ist:
> **Weitere Informationen → Trotzdem ausführen**.

### 2. yt-dlp und FFmpeg besorgen (erforderlich)
MediaForge nutzt zwei freie Werkzeuge, die **nicht** mitgeliefert werden:

- **yt-dlp** – für Downloads: <https://github.com/yt-dlp/yt-dlp/releases> (`yt-dlp.exe`)
- **FFmpeg** – für Schneiden, Konvertieren, Vorschau: <https://www.gyan.dev/ffmpeg/builds/> oder
  <https://github.com/BtbN/FFmpeg-Builds/releases> (benötigt: `ffmpeg.exe`, `ffprobe.exe`, `ffplay.exe`)

**Am einfachsten:** In MediaForge den Bereich **yt-dlp** öffnen und auf **yt-dlp Update** bzw.
**FFmpeg Update** klicken – beide Werkzeuge werden dann automatisch heruntergeladen.

**Alternativ manuell:** Die Exe-Dateien neben `MediaForge.exe` legen oder in den **Einstellungen**
den Pfad angeben. MediaForge sucht in dieser Reihenfolge:

1. in den Einstellungen hinterlegter Pfad
2. Ordner der `MediaForge.exe` und Arbeitsordner
3. Unterordner `Weiteres` bzw. `ffmpeg\bin`
4. `%LocalAppData%\MediaForge\tools`
5. Windows-`PATH`

### 3. Einstellungen (optional)
Über **Einstellungen** im Hauptmenü:

- **Zielordner** – Standard ist `Videos\MediaForge` im Benutzerprofil (wird automatisch angelegt),
  zusätzlich ein eigener Zielordner
- **Werkzeugpfade** für `yt-dlp.exe` und `ffmpeg.exe`
- **URLs-Datei** für den Massen-Import
- **Cookies** – aus (Standard), aus einem Browser (Firefox, Chrome, Edge, Brave, optional mit Profil)
  oder aus einer `cookies.txt`. Nötig für Inhalte, die eine Anmeldung erfordern.
- **Sorter** – Startordner, Zielbereiche und Tasten

## Bedienung

### Download
1. URL einfügen, Modus (Video/MP3), Qualität und Format wählen, optional einen Zeitbereich.
2. Zur Warteschlange hinzufügen und starten. Fortschritt und Log erscheinen unten.

**URL-Liste importieren:** Beim ersten Klick auf *Import* wird eine Vorlage `urls.txt` angelegt.
Eine URL pro Zeile, `#` leitet einen Kommentar ein, optional dahinter Zeitbereich und/oder `mp3`:

```text
https://www.youtube.com/watch?v=XXXXXXXXXXX
https://www.youtube.com/watch?v=YYYYYYYYYYY 00:00:10-00:02:30 mp3
```

### Cut, Editor, Convert, Speed Zone, Link
Datei öffnen, im Player bzw. auf der Zeitleiste Bereiche wählen und exportieren. Der Editor speichert
Marker und Bereiche als `<video>.mediaforge.json` neben dem Video, damit sie beim nächsten Öffnen
wieder da sind.

### Sorter
Startordner und Zielbereiche festlegen, dann Datei für Datei einsortieren:

| Taste | Aktion |
| --- | --- |
| `1`–`9`, `0`, `Q`–`P` | in den jeweiligen Zielbereich legen (eigene Tasten in den Einstellungen möglich) |
| `Leertaste` | Video abspielen/pausieren |
| `←` / `→` | vorherige/nächste Datei |
| `S` | überspringen |
| `Strg+Z` | letzte Ablage rückgängig |
| `F5` | Ordner neu einlesen |

Standardmäßig wird verschoben (optional kopiert); vorhandene Dateien werden nie überschrieben.

## Wo liegen die Daten?

| Was | Ort |
| --- | --- |
| Einstellungen | `%AppData%\MediaForge\settings.json` |
| Sorter-Fortschritt | `%AppData%\MediaForge\sorter\` |
| Heruntergeladene Werkzeuge | neben der Exe, sonst `%LocalAppData%\MediaForge\tools` |
| Downloads | `Videos\MediaForge` (einstellbar) |

Zum Deinstallieren die Exe und die genannten Ordner löschen.

## Selbst bauen

Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# Entwickeln / starten
dotnet run --project .\MediaForge\MediaForge.csproj -c Release -p:Platform=x64

# Portable Einzel-Exe nach .\release\MediaForge.exe
dotnet publish .\MediaForge\MediaForge.csproj -p:PublishProfile=Portable

# UI-Smoke-Tests
dotnet run --project .\MediaForge.SmokeTests\MediaForge.SmokeTests.csproj -c Release
```

Technik: C# / .NET 10, WPF, MVVM, keine NuGet-Abhängigkeiten. Details zur Architektur in
[`MediaForge/README.md`](MediaForge/README.md).

## Rechtlicher Hinweis

MediaForge ist nur eine Oberfläche. Lade nur Inhalte herunter, zu deren Nutzung du berechtigt bist,
und beachte die Nutzungsbedingungen der jeweiligen Plattform. yt-dlp und FFmpeg stehen unter ihren
eigenen Lizenzen (Unlicense bzw. LGPL/GPL).

## Lizenz

[MIT](LICENSE) © 2026 mRbRaIn0
