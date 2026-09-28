using System.Text.Json;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed class AppSettings
{
    public const string CookieModeNone = "none";
    public const string CookieModeBrowser = "browser";
    public const string CookieModeFile = "file";

    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public string CustomOutputDirectory { get; set; } = string.Empty;
    public string YtDlpPath { get; set; } = "yt-dlp.exe";
    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public string UrlsFile { get; set; } = "urls.txt";
    public string CookieMode { get; set; } = CookieModeNone;
    public string CookieBrowser { get; set; } = "firefox";
    public string CookieProfile { get; set; } = string.Empty;
    public string CookieFile { get; set; } = "cookies.txt";

    /// <summary>Startordner des Sorters — leer bedeutet: im Sorter noch keinen Ordner gewählt.</summary>
    public string SorterSourceDirectory { get; set; } = string.Empty;

    /// <summary>Frei einstellbare Zielbereiche des Sorters; die Reihenfolge bestimmt die automatische Tastenbelegung.</summary>
    public List<SorterTarget> SorterTargets { get; set; } = [];

    public bool SorterIncludeSubfolders { get; set; } = true;
    public bool SorterPreserveSubfolders { get; set; } = true;
    public bool SorterCopyMode { get; set; }
    public bool SorterBackupEnabled { get; set; }
    public string SorterBackupDirectory { get; set; } = string.Empty;
    public bool SorterAutoPlay { get; set; } = true;

    /// <summary>Neutraler Standard-Zielordner „Videos\MediaForge“ im Benutzerprofil; wird beim ersten Download angelegt.</summary>
    public static string StandardOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MediaForge");

    public static AppSettings CreateDefault() => new()
    {
        DefaultOutputDirectory = StandardOutputDirectory
    };

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SorterTargets = SorterTargets.Select(target => target.Clone()).ToList();
        return copy;
    }
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static AppSettings _current = Load();

    public static AppSettings Current => _current;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaForge", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null) return Normalize(loaded);
            }
        }
        catch { /* Beschädigte Datei: Standardwerte verwenden. */ }
        return AppSettings.CreateDefault();
    }

    public static bool Save(AppSettings settings)
    {
        _current = Normalize(settings.Clone());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, Options));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Standard-Zielordner; leer bedeutet „Videos\MediaForge“ im Benutzerprofil.</summary>
    public static string OutputDirectory(bool useCustom)
    {
        var custom = _current.CustomOutputDirectory.Trim();
        if (useCustom && custom.Length > 0) return custom;
        var standard = _current.DefaultOutputDirectory.Trim();
        return standard.Length > 0 ? standard : AppSettings.StandardOutputDirectory;
    }

    /// <summary>Löst „urls.txt“ bzw. die Cookie-Datei relativ zum App- oder Arbeitsordner auf.</summary>
    public static string ResolveDataFile(string value, string fallbackName)
    {
        var name = value.Trim().Trim('"');
        if (name.Length == 0) name = fallbackName;
        if (Path.IsPathRooted(name)) return name;
        var local = Path.Combine(ToolLocator.ApplicationDirectory, name);
        if (File.Exists(local)) return local;
        var working = Path.Combine(Environment.CurrentDirectory, name);
        return File.Exists(working) ? working : local;
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.DefaultOutputDirectory = settings.DefaultOutputDirectory.Trim();
        settings.CustomOutputDirectory = settings.CustomOutputDirectory.Trim();
        settings.YtDlpPath = settings.YtDlpPath.Trim();
        settings.FfmpegPath = settings.FfmpegPath.Trim();
        settings.UrlsFile = settings.UrlsFile.Trim();
        settings.CookieBrowser = settings.CookieBrowser.Trim().ToLowerInvariant();
        settings.CookieProfile = settings.CookieProfile.Trim();
        settings.CookieFile = settings.CookieFile.Trim();
        settings.CookieMode = settings.CookieMode.Trim().ToLowerInvariant() switch
        {
            AppSettings.CookieModeBrowser => AppSettings.CookieModeBrowser,
            AppSettings.CookieModeFile => AppSettings.CookieModeFile,
            _ => AppSettings.CookieModeNone
        };
        if (settings.CookieBrowser.Length == 0) settings.CookieBrowser = "firefox";
        settings.SorterSourceDirectory = settings.SorterSourceDirectory.Trim().Trim('"');
        settings.SorterTargets = settings.SorterTargets
            .Select(target => new SorterTarget
            {
                Name = target.Name.Trim(),
                Folder = target.Folder.Trim().Trim('"'),
                Key = target.Key.Trim().ToLowerInvariant() is { Length: > 0 } key ? key[..1] : string.Empty
            })
            .Where(target => target.Folder.Length > 0)
            .Select(target =>
            {
                if (target.Name.Length == 0)
                    target.Name = Path.GetFileName(target.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (target.Name.Length == 0) target.Name = target.Folder;
                return target;
            })
            .ToList();
        return settings;
    }
}
