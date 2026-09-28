# MediaForge

MediaForge ist die C#-/WPF-Neuimplementierung der beiden bisherigen Python-Anwendungen. Die App vereint `yt-dlp`, Cut, Editor, Convert, Speed Zone, Link und den Sorter in einem gemeinsamen Hauptmenü.

## Oberfläche

Alle Farben, Flächen und Control-Styles liegen in `Themes/PowerTheme.xaml` und werden in `App.xaml`
eingebunden. Die impliziten Basisstile (Button, TextBox, ComboBox, CheckBox, RadioButton, ProgressBar,
ListBox, ScrollBar) leiten per `BasedOn` von den `Power*`-Styles ab — neue Ansichten sehen dadurch
ohne Zutun richtig aus. Farben gehören nicht direkt ins View-XAML.

Jedes Modul hat einen festen Ton (`ModuleDownload`, `ModuleCut`, `ModuleEditor`, `ModuleConvert`,
`ModuleSpeed`, `ModuleLink`, `ModuleSorter`), der die Kachel im Hauptmenü und den Akzentbalken im Kopf
der jeweiligen Ansicht verbindet.

## Technik

- C# mit .NET 10 (`net10.0-windows`)
- WPF und XAML
- MVVM für Navigation, Zustand, Commands und Hintergrundaufgaben
- C#-Code-Behind nur für native Medienwiedergabe, Timeline-Seeking und fensterspezifische Interaktion
- Keine NuGet-Abhängigkeiten

## Build

```powershell
dotnet build ..\MediaForge.sln -c Release -p:Platform=x64
```

Start aus dem Projektordner:

```powershell
dotnet run --project .\MediaForge.csproj -c Release -p:Platform=x64
```

## Portable Exe (Release)

```powershell
dotnet publish .\MediaForge.csproj -p:PublishProfile=Portable
```

Ergebnis ist eine einzelne `release\MediaForge.exe` ohne weitere Dateien und ohne nötige
.NET-Installation. Beim ersten Start gilt:

- Downloads landen standardmäßig in `Videos\MediaForge` im Benutzerprofil (wird bei Bedarf angelegt).
- **yt-dlp Update** / **FFmpeg Update** legen die Werkzeuge neben die Exe; ist der Ordner nicht
  beschreibbar, nach `%LocalAppData%\MediaForge\tools`.
- Cookies sind aus; Browser, Profil oder Cookie-Datei stellt man in den Einstellungen ein.
- Die URLs-Datei wird beim ersten **Import** als Vorlage angelegt.

## Einstellungen

Die Schaltfläche **Einstellungen** im Hauptmenü öffnet den Dialog für Pfade und Cookies:

- Standard-Zielordner und eigener Zielordner (Schalter „Eigenen Zielordner nutzen“ im yt-dlp Bereich)
- Pfade zu `yt-dlp.exe` und `ffmpeg.exe` (`ffprobe.exe`/`ffplay.exe` werden im selben Ordner erwartet)
- URLs-Datei für den Import
- Cookie-Modus (keine / Browser / Datei), Browser, Browser-Profil und Cookie-Datei
- File Sorter: Startordner, Zielbereiche (Name, Ordner, optionale Taste) und die Schalter
  „Unterordner einbeziehen“, „Unterordner im Ziel behalten“, „Kopieren statt verschieben“ und
  „Videos automatisch abspielen“

Gespeichert wird nach `%AppData%\MediaForge\settings.json`. Ein leerer bzw. relativer Werkzeugpfad
(z. B. `ffmpeg.exe`) aktiviert die automatische Suche.

## Externe Werkzeuge

Die App sucht `yt-dlp.exe`, `ffmpeg.exe`, `ffprobe.exe` und `ffplay.exe` in dieser Reihenfolge:

1. der in den Einstellungen hinterlegte Pfad
2. App- und Arbeitsordner
3. übergeordneten Projektordnern und dem alten Ordner `kontext und alt`
4. `Weiteres` bzw. `ffmpeg\bin`
5. `%LocalAppData%\MediaForge\tools`
6. `PATH`

Fehlende Werkzeuge lädt der yt-dlp Bereich über **yt-dlp Update** bzw. **FFmpeg Update**; beide
schreiben ihren Fortschritt in das dortige Ausgabe-/Log-Fenster. Real-ESRGAN wird wie bisher unter
`Weiteres\realesrgan-ncnn-vulkan-20220424-windows` erwartet.

## Sorter

Der Sorter geht den Startordner Datei für Datei durch und legt jede Datei per Tastendruck oder Klick in
einem der eingestellten Zielbereiche ab. Startordner und Zielbereiche sind frei wählbar; nichts ist im
Code festgelegt.

- **Vorschau je Dateityp:** Videos laufen im Player der App (Abspielen/Pause, Stop, Position,
  Lautstärke), Fotos werden als Standbild gezeigt, alle übrigen Dateien nur mit Typ, Name und Größe.
- **Tasten:** `1`–`9`, `0`, danach `Q W E R T Z U I O P` werden den Zielbereichen der Reihe nach
  zugeteilt. In den Einstellungen lässt sich pro Bereich eine eigene Taste eintragen; sie hat Vorrang.
- **Weitere Tasten:** `Leertaste` (abspielen/pausieren), `←`/`→` (vorherige/nächste Datei), `S`
  (überspringen), `L` (neu laden), `Strg+Z` (letzte Ablage zurücknehmen), `F5` (neu einlesen).
- **Ablage:** standardmäßig verschieben, auf Wunsch kopieren. Ein belegter Dateiname im Ziel bekommt
  automatisch eine Nummer, es wird nichts überschrieben. Zielordner, die im Startordner liegen, werden
  beim Einlesen übersprungen.
- **Fortschritt:** je Startordner unter `%AppData%\MediaForge\sorter\<Ordner>-<Prüfsumme>.json`.
- **Backup:** Checkbox neben „Startordner“ aktivieren und einen Backup-Ordner wählen. Nach dem
  Einsortieren werden Bilder und Videos zusätzlich kopiert. Vorhandene Unterordner werden über
  übereinstimmende Pfadenden zugeordnet; die längste eindeutige Übereinstimmung gewinnt. Fehlt ein
  Treffer, entsteht ein Ordner mit dem Namen des Sortierziels. Beibehaltene Unterordner bleiben auch
  im Backup erhalten. Mehrdeutige Treffer stoppen die Ablage, belegte Dateinamen erhalten eine Nummer.
  Bei einem Kopierfehler bleibt die erfolgreiche Hauptablage bestehen und eine Fehlermeldung erscheint.
  Rückgängig nimmt nur die Hauptablage zurück; Backup-Kopien bleiben erhalten. Ausgeschaltet bleibt
  das bisherige Verhalten bestehen. Ordner und Aktivierung werden gespeichert.
  Der Backup-Baum wird beim Aktivieren einmal im Hintergrund indexiert und danach wiederverwendet;
  **Neu einlesen (F5)** aktualisiert den Index nach externen Ordneränderungen.
