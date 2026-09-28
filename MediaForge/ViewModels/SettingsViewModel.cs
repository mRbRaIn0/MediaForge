using System.Collections.ObjectModel;
using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

/// <summary>Eine Zeile der Zielbereichs-Liste in den Einstellungen.</summary>
public sealed class SorterTargetRow : ObservableObject
{
    private string _name = string.Empty;
    private string _folder = string.Empty;
    private string _key = string.Empty;
    private string _effectiveKey = string.Empty;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Folder { get => _folder; set => SetProperty(ref _folder, value); }

    /// <summary>Fest vergebene Taste; leer bedeutet, dass die Reihenfolge die Taste bestimmt.</summary>
    public string Key
    {
        get => _key;
        set
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 1) trimmed = trimmed[..1];
            if (SetProperty(ref _key, trimmed.ToLowerInvariant())) KeyChanged?.Invoke();
        }
    }

    /// <summary>Taste, die im Sorter tatsaechlich wirkt - inklusive der automatisch vergebenen.</summary>
    public string EffectiveKey { get => _effectiveKey; set => SetProperty(ref _effectiveKey, value); }

    public event Action? KeyChanged;

    public SorterTarget ToTarget() => new() { Name = Name.Trim(), Folder = Folder.Trim(), Key = Key };
}

public sealed class SettingsViewModel : ObservableObject
{
    private const string ModeNone = "Keine";
    private const string ModeBrowser = "Browser";
    private const string ModeFile = "Cookie-Datei";

    private string _defaultOutputDirectory = string.Empty;
    private string _customOutputDirectory = string.Empty;
    private string _ytDlpPath = string.Empty;
    private string _ffmpegPath = string.Empty;
    private string _urlsFile = string.Empty;
    private string _cookieMode = ModeNone;
    private string _cookieBrowser = string.Empty;
    private string _cookieProfile = string.Empty;
    private string _cookieFile = string.Empty;
    private string _sorterSourceDirectory = string.Empty;
    private bool _sorterIncludeSubfolders = true;
    private bool _sorterPreserveSubfolders = true;
    private bool _sorterCopyMode;
    private bool _sorterAutoPlay = true;

    public SettingsViewModel(AppSettings settings)
    {
        BrowseCommand = new RelayCommand(p => Browse(p?.ToString()));
        ResetCommand = new RelayCommand(_ => Load(AppSettings.CreateDefault()));
        AddTargetCommand = new RelayCommand(_ => AddTarget());
        RemoveTargetCommand = new RelayCommand(p => RemoveTarget(p as SorterTargetRow));
        MoveTargetUpCommand = new RelayCommand(p => MoveTarget(p as SorterTargetRow, -1));
        MoveTargetDownCommand = new RelayCommand(p => MoveTarget(p as SorterTargetRow, 1));
        BrowseTargetCommand = new RelayCommand(p => BrowseTarget(p as SorterTargetRow));
        Load(settings);
    }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand RemoveTargetCommand { get; }
    public RelayCommand MoveTargetUpCommand { get; }
    public RelayCommand MoveTargetDownCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public ObservableCollection<SorterTargetRow> SorterTargets { get; } = [];
    public string[] CookieModes { get; } = [ModeNone, ModeBrowser, ModeFile];
    public string[] Browsers { get; } = ["chrome", "firefox", "edge", "brave", "chromium", "opera", "vivaldi", "safari", "whale"];
    public string SettingsFilePath => SettingsService.FilePath;

    public string DefaultOutputDirectory { get => _defaultOutputDirectory; set => SetProperty(ref _defaultOutputDirectory, value); }
    public string CustomOutputDirectory { get => _customOutputDirectory; set => SetProperty(ref _customOutputDirectory, value); }
    public string YtDlpPath { get => _ytDlpPath; set => SetProperty(ref _ytDlpPath, value); }
    public string FfmpegPath { get => _ffmpegPath; set => SetProperty(ref _ffmpegPath, value); }
    public string UrlsFile { get => _urlsFile; set => SetProperty(ref _urlsFile, value); }
    public string CookieMode { get => _cookieMode; set { if (SetProperty(ref _cookieMode, value)) { OnPropertyChanged(nameof(IsBrowserMode)); OnPropertyChanged(nameof(IsFileMode)); } } }
    public string CookieBrowser { get => _cookieBrowser; set => SetProperty(ref _cookieBrowser, value); }
    public string CookieProfile { get => _cookieProfile; set => SetProperty(ref _cookieProfile, value); }
    public string CookieFile { get => _cookieFile; set => SetProperty(ref _cookieFile, value); }
    public string SorterSourceDirectory { get => _sorterSourceDirectory; set => SetProperty(ref _sorterSourceDirectory, value); }
    public bool SorterIncludeSubfolders { get => _sorterIncludeSubfolders; set => SetProperty(ref _sorterIncludeSubfolders, value); }
    public bool SorterPreserveSubfolders { get => _sorterPreserveSubfolders; set => SetProperty(ref _sorterPreserveSubfolders, value); }
    public bool SorterCopyMode { get => _sorterCopyMode; set => SetProperty(ref _sorterCopyMode, value); }
    public bool SorterAutoPlay { get => _sorterAutoPlay; set => SetProperty(ref _sorterAutoPlay, value); }
    public bool IsBrowserMode => CookieMode == ModeBrowser;
    public bool IsFileMode => CookieMode == ModeFile;

    public AppSettings ToSettings() => new()
    {
        DefaultOutputDirectory = DefaultOutputDirectory,
        CustomOutputDirectory = CustomOutputDirectory,
        YtDlpPath = YtDlpPath,
        FfmpegPath = FfmpegPath,
        UrlsFile = UrlsFile,
        CookieMode = CookieMode switch
        {
            ModeBrowser => AppSettings.CookieModeBrowser,
            ModeFile => AppSettings.CookieModeFile,
            _ => AppSettings.CookieModeNone
        },
        CookieBrowser = CookieBrowser,
        CookieProfile = CookieProfile,
        CookieFile = CookieFile,
        SorterSourceDirectory = SorterSourceDirectory,
        SorterTargets = SorterTargets.Select(row => row.ToTarget()).Where(target => target.Folder.Length > 0).ToList(),
        SorterIncludeSubfolders = SorterIncludeSubfolders,
        SorterPreserveSubfolders = SorterPreserveSubfolders,
        SorterCopyMode = SorterCopyMode,
        SorterAutoPlay = SorterAutoPlay
    };

    private void Load(AppSettings settings)
    {
        DefaultOutputDirectory = settings.DefaultOutputDirectory;
        CustomOutputDirectory = settings.CustomOutputDirectory;
        YtDlpPath = settings.YtDlpPath;
        FfmpegPath = settings.FfmpegPath;
        UrlsFile = settings.UrlsFile;
        CookieMode = settings.CookieMode switch
        {
            AppSettings.CookieModeBrowser => ModeBrowser,
            AppSettings.CookieModeFile => ModeFile,
            _ => ModeNone
        };
        CookieBrowser = settings.CookieBrowser;
        CookieProfile = settings.CookieProfile;
        CookieFile = settings.CookieFile;
        SorterSourceDirectory = settings.SorterSourceDirectory;
        SorterIncludeSubfolders = settings.SorterIncludeSubfolders;
        SorterPreserveSubfolders = settings.SorterPreserveSubfolders;
        SorterCopyMode = settings.SorterCopyMode;
        SorterAutoPlay = settings.SorterAutoPlay;
        foreach (var row in SorterTargets) row.KeyChanged -= RefreshKeys;
        SorterTargets.Clear();
        foreach (var target in settings.SorterTargets) SorterTargets.Add(Row(target));
        RefreshKeys();
    }

    private SorterTargetRow Row(SorterTarget target)
    {
        var row = new SorterTargetRow { Name = target.Name, Folder = target.Folder, Key = target.Key };
        row.KeyChanged += RefreshKeys;
        return row;
    }

    private void AddTarget()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Zielordner waehlen",
            InitialDirectory = ExistingDirectory(SorterSourceDirectory)
        };
        if (dialog.ShowDialog() != true) return;
        SorterTargets.Add(Row(new SorterTarget { Name = FolderName(dialog.FolderName), Folder = dialog.FolderName }));
        RefreshKeys();
    }

    private void RemoveTarget(SorterTargetRow? row)
    {
        if (row is null) return;
        row.KeyChanged -= RefreshKeys;
        SorterTargets.Remove(row);
        RefreshKeys();
    }

    private void MoveTarget(SorterTargetRow? row, int delta)
    {
        if (row is null) return;
        var index = SorterTargets.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= SorterTargets.Count) return;
        SorterTargets.Move(index, target);
        RefreshKeys();
    }

    private void BrowseTarget(SorterTargetRow? row)
    {
        if (row is null) return;
        PickFolder(row.Folder, value =>
        {
            row.Folder = value;
            if (row.Name.Trim().Length == 0) row.Name = FolderName(value);
        });
    }

    /// <summary>Vergibt die Tasten wie der Sorter: erst die fest eingetragenen, den Rest der Reihe nach.</summary>
    private void RefreshKeys()
    {
        var used = SorterTargets.Where(row => row.Key.Length > 0)
            .Select(row => char.ToLowerInvariant(row.Key[0]))
            .ToHashSet();
        var pool = new Queue<char>(SorterService.KeyPool.Where(key => !used.Contains(key)));
        foreach (var row in SorterTargets)
            row.EffectiveKey = row.Key.Length > 0
                ? char.ToUpperInvariant(row.Key[0]).ToString()
                : pool.Count > 0 ? char.ToUpperInvariant(pool.Dequeue()).ToString() : "-";
    }

    private static string FolderName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : folder;
    }

    private void Browse(string? field)
    {
        switch (field)
        {
            case "default":
                PickFolder(DefaultOutputDirectory, value => DefaultOutputDirectory = value); break;
            case "custom":
                PickFolder(CustomOutputDirectory, value => CustomOutputDirectory = value); break;
            case "ytdlp":
                PickFile(YtDlpPath, "yt-dlp|yt-dlp.exe|Programme|*.exe|Alle Dateien|*.*", value => YtDlpPath = value); break;
            case "ffmpeg":
                PickFile(FfmpegPath, "ffmpeg|ffmpeg.exe|Programme|*.exe|Alle Dateien|*.*", value => FfmpegPath = value); break;
            case "urls":
                PickFile(UrlsFile, "Textdateien|*.txt|Alle Dateien|*.*", value => UrlsFile = value); break;
            case "cookies":
                PickFile(CookieFile, "Cookie-Dateien|*.txt|Alle Dateien|*.*", value => CookieFile = value); break;
            case "sortersource":
                PickFolder(SorterSourceDirectory, value => SorterSourceDirectory = value); break;
        }
    }

    private static void PickFolder(string current, Action<string> apply)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner auswählen", InitialDirectory = ExistingDirectory(current) };
        if (dialog.ShowDialog() == true) apply(dialog.FolderName);
    }

    private static void PickFile(string current, string filter, Action<string> apply)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Datei auswählen",
            Filter = filter,
            CheckFileExists = true,
            InitialDirectory = ExistingDirectory(current)
        };
        if (dialog.ShowDialog() == true) apply(dialog.FileName);
    }

    private static string ExistingDirectory(string value)
    {
        var candidate = value.Trim().Trim('"');
        if (candidate.Length == 0) return ToolLocator.ApplicationDirectory;
        if (Directory.Exists(candidate)) return candidate;
        try
        {
            var directory = Path.GetDirectoryName(Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(ToolLocator.ApplicationDirectory, candidate));
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory;
        }
        catch { /* Ungültige Eingabe: Anwendungsordner verwenden. */ }
        return ToolLocator.ApplicationDirectory;
    }
}
