using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

/// <summary>Ein Zielbereich in der Seitenleiste — Taste, Zustand und Zähler dieser Sitzung.</summary>
public sealed class SorterSlot : ObservableObject
{
    private int _count;
    private bool _isLast;

    public SorterSlot(SorterTarget target, string key)
    {
        Target = target;
        Key = key;
    }

    public SorterTarget Target { get; }
    public string Key { get; }
    public string KeyLabel => Key.Length == 0 ? "·" : Key.ToUpperInvariant();
    public string Name => Target.Name;
    public string Folder => Target.Folder;
    public bool Exists => !string.IsNullOrWhiteSpace(Folder) && Directory.Exists(Folder);
    public string FolderText => Exists ? Folder : $"{Folder}   (wird beim ersten Ablegen angelegt)";
    public int Count { get => _count; set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(CountText)); } }
    public string CountText => Count == 0 ? string.Empty : $"+{Count}";
    public bool IsLast { get => _isLast; set => SetProperty(ref _isLast, value); }

    /// <summary>Nach der ersten Ablage existiert der Ordner — der Hinweis in der Zeile muss dann weg.</summary>
    public void RefreshFolderState()
    {
        OnPropertyChanged(nameof(Exists));
        OnPropertyChanged(nameof(FolderText));
    }
}

/// <summary>
/// Der File Sorter: liest den Startordner ein, zeigt jede Datei passend zu ihrem Typ und legt sie
/// per Tastendruck oder Klick in einem der eingestellten Zielbereiche ab.
/// </summary>
public sealed class SorterViewModel : ObservableObject, IToolAware
{
    public const string FilterAll = "Alle Dateien";
    public const string FilterVideos = "Nur Videos";
    public const string FilterImages = "Nur Bilder";
    public const string FilterOther = "Nur sonstige Dateien";

    private readonly Action _back;
    private readonly List<UndoStep> _undo = [];
    private SorterProgress _progress = new();
    private List<SorterItem> _all = [];
    private List<SorterItem> _items = [];
    private int _index;
    private string _source = string.Empty;
    private string _filter = FilterAll;
    private string _status = "Startordner in den Einstellungen festlegen oder oben rechts wählen.";
    private bool _statusIsError;
    private BitmapImage? _previewImage;
    private bool _busy;
    private string _settingsSignature = string.Empty;
    private Task<BackupFolderIndex>? _backupIndexTask;
    private string _backupIndexRoot = string.Empty;

    private sealed record UndoStep(SorterItem Item, int Index, SorterProgressEntry Entry, SorterSlot Slot);

    public SorterViewModel(Action back)
    {
        _back = back;
        BackCommand = new RelayCommand(_ => { ReleaseRequested?.Invoke(); _back(); });
        ChooseSourceCommand = new RelayCommand(_ => ChooseSource());
        ChooseBackupCommand = new RelayCommand(_ => ChooseBackup());
        RescanCommand = new RelayCommand(_ => Rescan(announce: true));
        OpenSourceCommand = new RelayCommand(_ => Reveal(SourceDirectory), _ => Directory.Exists(SourceDirectory));
        RevealCommand = new RelayCommand(_ => Reveal(Current?.FullPath), _ => Current is not null);
        SortCommand = new AsyncRelayCommand(parameter => SortAsync(parameter as SorterSlot), _ => !_busy && Current is not null);
        SkipCommand = new RelayCommand(_ => Move(1, "Übersprungen."), _ => Current is not null);
        NextCommand = new RelayCommand(_ => Move(1, null), _ => Current is not null);
        PreviousCommand = new RelayCommand(_ => Move(-1, null), _ => Current is not null);
        ReloadCommand = new RelayCommand(_ => { ShowCurrent(); SetStatus("Datei neu geladen."); }, _ => Current is not null);
        UndoCommand = new AsyncRelayCommand(_ => UndoAsync(), _ => !_busy && _undo.Count > 0);
        LoadSettings(initial: true);
    }

    /// <summary>Wird ausgelöst, bevor eine Datei angefasst wird — die Ansicht gibt den Player frei.</summary>
    public event Action? ReleaseRequested;

    /// <summary>Meldet der Ansicht, welche Datei jetzt zu zeigen ist (null = nichts mehr offen).</summary>
    public event Action<SorterItem?>? CurrentChanged;

    public RelayCommand BackCommand { get; }
    public RelayCommand ChooseSourceCommand { get; }
    public RelayCommand ChooseBackupCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public RelayCommand RevealCommand { get; }
    public AsyncRelayCommand SortCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }

    public ObservableCollection<SorterSlot> Targets { get; } = [];
    public string[] Filters { get; } = [FilterAll, FilterVideos, FilterImages, FilterOther];

    public string SourceDirectory
    {
        get => _source;
        private set
        {
            if (!SetProperty(ref _source, value)) return;
            OnPropertyChanged(nameof(SourceDisplay));
            OnPropertyChanged(nameof(HasSource));
            OpenSourceCommand.RaiseCanExecuteChanged();
        }
    }

    public string SourceDisplay => SourceDirectory.Length == 0 ? "Kein Startordner gewählt" : SourceDirectory;
    public bool HasSource => SourceDirectory.Length > 0 && Directory.Exists(SourceDirectory);
    public bool HasTargets => Targets.Count > 0;
    public bool AutoPlay => SettingsService.Current.SorterAutoPlay;
    public bool CopyMode => SettingsService.Current.SorterCopyMode;
    public bool IsIdle => !_busy;
    public bool BackupEnabled
    {
        get => SettingsService.Current.SorterBackupEnabled;
        set
        {
            if (_busy || value == BackupEnabled) return;
            var settings = SettingsService.Current.Clone();
            settings.SorterBackupEnabled = value;
            SettingsService.Save(settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackupTooltip));
            PrepareBackupIndex();
            Rescan(announce: false);
            if (value && string.IsNullOrWhiteSpace(BackupDirectory)) ChooseBackup();
        }
    }
    public string BackupDirectory => SettingsService.Current.SorterBackupDirectory;
    public string BackupLabel => string.IsNullOrWhiteSpace(BackupDirectory)
        ? "Backup-Ordner" : $"Backup: {Path.GetFileName(BackupDirectory.TrimEnd(Path.DirectorySeparatorChar))}";
    public string BackupTooltip => $"{(BackupEnabled ? "Aktiv" : "Ausgeschaltet")}: {(string.IsNullOrWhiteSpace(BackupDirectory) ? "Backup-Ordner wählen" : BackupDirectory)}\nBilder und Videos zusätzlich in passende Unterordner kopieren. Backups bleiben beim Rückgängigmachen erhalten.";
    public string ModeText => CopyMode ? "Kopieren" : "Verschieben";

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            Rebuild(null);
            ShowCurrent();
        }
    }

    public SorterItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    public bool HasCurrent => Current is not null;
    public bool IsVideo => Current?.Kind == SorterItemKind.Video;
    public bool IsImage => Current?.Kind == SorterItemKind.Image;
    public bool IsPlainFile => Current?.Kind == SorterItemKind.File;
    public string CurrentName => Current?.Name ?? "Keine Datei offen";
    public string CurrentSubfolder => Current is { Subfolder.Length: > 0 } item ? item.Subfolder : "Hauptordner";
    public string CurrentPath => Current?.FullPath ?? string.Empty;
    public string CurrentBadge => Current is null ? string.Empty : Current.Extension.Length > 0 ? Current.Extension : "DATEI";

    public string CurrentMeta => Current is null
        ? string.Empty
        : $"{KindText(Current.Kind)}  ·  {Formatters.Size(Current.Size)}  ·  {CurrentSubfolder}";

    public BitmapImage? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }

    public string CounterText => _items.Count == 0 ? "0 / 0" : $"{_index + 1} / {_items.Count}";
    public int SortedCount => _progress.Entries.Count;
    public int RemainingCount => _items.Count;

    public string ProgressText => _items.Count == 0 && SortedCount == 0
        ? "Nichts einzulesen"
        : $"{SortedCount} einsortiert  ·  {RemainingCount} offen";

    public double ProgressValue
    {
        get
        {
            var total = SortedCount + RemainingCount;
            return total == 0 ? 0 : (double)SortedCount / total;
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

    /// <summary>Wird nach dem Speichern der Einstellungen aufgerufen: Ziele neu aufbauen, bei neuem Startordner neu einlesen.</summary>
    public void RefreshTools() => LoadSettings(initial: false);

    /// <summary>Meldung aus der Ansicht, etwa wenn der Player eine Datei nicht abspielen kann.</summary>
    public void Notify(string text, bool isError = false) => SetStatus(text, isError);

    /// <summary>Tastendruck aus der Ansicht — liefert true, wenn die Taste zu einem Zielbereich gehört.</summary>
    public bool TrySortByKey(string key)
    {
        if (key.Length == 0 || _busy || Current is null) return false;
        var slot = Targets.FirstOrDefault(t => t.Key.Length > 0 && t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (slot is null) return false;
        SortCommand.Execute(slot);
        return true;
    }

    /// <summary>
    /// Uebernimmt Startordner, Zielbereiche und Schalter aus den Einstellungen und liest neu ein,
    /// sobald sich daran etwas geaendert hat.
    /// </summary>
    private void LoadSettings(bool initial)
    {
        var settings = SettingsService.Current;
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(AutoPlay));
        OnPropertyChanged(nameof(CopyMode));
        OnPropertyChanged(nameof(BackupEnabled));
        OnPropertyChanged(nameof(BackupDirectory));
        OnPropertyChanged(nameof(BackupLabel));
        OnPropertyChanged(nameof(BackupTooltip));

        var signature = string.Join('|', settings.SorterTargets
            .Select(target => $"{target.Name}\u001f{target.Folder}\u001f{target.Key}")
            .Prepend(settings.SorterIncludeSubfolders.ToString())
            .Prepend(settings.SorterSourceDirectory)
            .Prepend(settings.SorterBackupEnabled.ToString())
            .Prepend(settings.SorterBackupDirectory));
        if (!initial && signature == _settingsSignature)
        {
            RaiseListState();
            return;
        }

        _settingsSignature = signature;
        BuildTargets(settings.SorterTargets);
        SourceDirectory = settings.SorterSourceDirectory;
        PrepareBackupIndex();
        Rescan(announce: false);
    }

    private void BuildTargets(IReadOnlyList<SorterTarget> targets)
    {
        var used = new HashSet<char>(targets
            .Where(t => t.Key.Length > 0)
            .Select(t => char.ToLowerInvariant(t.Key[0])));
        var pool = new Queue<char>(SorterService.KeyPool.Where(c => !used.Contains(c)));

        Targets.Clear();
        foreach (var target in targets)
        {
            var key = target.Key.Length > 0
                ? char.ToLowerInvariant(target.Key[0]).ToString()
                : pool.Count > 0 ? pool.Dequeue().ToString() : string.Empty;
            Targets.Add(new SorterSlot(target.Clone(), key));
        }
        OnPropertyChanged(nameof(HasTargets));
    }

    private void ChooseSource()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Startordner wählen",
            InitialDirectory = Directory.Exists(SourceDirectory) ? SourceDirectory : ToolLocator.ApplicationDirectory
        };
        if (dialog.ShowDialog() != true) return;

        ReleaseRequested?.Invoke();
        var settings = SettingsService.Current.Clone();
        settings.SorterSourceDirectory = dialog.FolderName;
        SettingsService.Save(settings);
        LoadSettings(initial: false);
        if (_items.Count > 0) SetStatus($"{_items.Count} Dateien eingelesen.");
    }

    private void ChooseBackup()
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Backup-Ordner wählen",
            InitialDirectory = Directory.Exists(BackupDirectory) ? BackupDirectory : ToolLocator.ApplicationDirectory
        };
        if (dialog.ShowDialog() != true) return;
        if (HasSource)
        {
            var relative = Path.GetRelativePath(dialog.FolderName, SourceDirectory);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar)))
            {
                SetStatus("Der Backup-Ordner darf den Startordner nicht enthalten oder mit ihm identisch sein.", isError: true);
                return;
            }
        }
        var settings = SettingsService.Current.Clone();
        settings.SorterBackupDirectory = dialog.FolderName;
        SettingsService.Save(settings);
        LoadSettings(initial: false);
    }

    /// <summary>Startet den einmaligen Scan frühzeitig, damit beim ersten Sortieren möglichst keine Wartezeit entsteht.</summary>
    private void PrepareBackupIndex()
    {
        var root = BackupEnabled && Directory.Exists(BackupDirectory)
            ? Path.GetFullPath(BackupDirectory) : string.Empty;
        if (root.Equals(_backupIndexRoot, StringComparison.OrdinalIgnoreCase) && _backupIndexTask is not null) return;
        _backupIndexRoot = root;
        var targetFolders = Targets.Select(slot => slot.Folder).Where(folder => folder.Length > 0).ToArray();
        _backupIndexTask = root.Length == 0 ? null
            : Task.Run(() => SorterService.CreateBackupFolderIndex(root, targetFolders));
    }

    private async Task<string> ResolveBackupFolderAsync(string targetFolder)
    {
        PrepareBackupIndex();
        if (_backupIndexTask is null) throw new DirectoryNotFoundException("Backup-Ordner nicht gefunden.");
        if (SorterService.TryResolveDirectBackupFolder(BackupDirectory, targetFolder, out var direct)) return direct;
        var index = await _backupIndexTask;
        return SorterService.ResolveBackupFolder(index, targetFolder);
    }

    private void Rescan(bool announce)
    {
        ReleaseRequested?.Invoke();
        if (announce)
        {
            _backupIndexRoot = string.Empty;
            _backupIndexTask = null;
            PrepareBackupIndex();
        }
        _undo.Clear();
        UndoCommand.RaiseCanExecuteChanged();
        _progress = SorterService.LoadProgress(SourceDirectory);

        var scanned = SorterService.Scan(SourceDirectory, SettingsService.Current.SorterIncludeSubfolders,
            Targets.Select(slot => slot.Target), BackupEnabled ? BackupDirectory : null);
        var done = _progress.Entries
            .Where(entry => entry.Destination.Length > 0 && File.Exists(entry.Destination))
            .Select(entry => entry.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _all = scanned.Where(item => !done.Contains(item.RelativePath)).ToList();

        _index = 0;
        Rebuild(null);
        ShowCurrent();

        if (!HasSource)
            SetStatus(SourceDirectory.Length == 0
                ? "Kein Startordner gewählt — oben rechts wählen oder in den Einstellungen festlegen."
                : $"Startordner nicht gefunden: {SourceDirectory}", isError: SourceDirectory.Length > 0);
        else if (_items.Count == 0)
            SetStatus("Keine passenden Dateien im Startordner.");
        else
            SetStatus(announce
                ? $"{_items.Count} Dateien eingelesen."
                : $"{_items.Count} Dateien offen — Taste drücken oder Zielbereich anklicken.");
    }

    /// <summary>Baut die gefilterte Liste neu auf und hält dabei nach Möglichkeit die angezeigte Datei fest.</summary>
    private void Rebuild(SorterItem? keep)
    {
        keep ??= Current;
        _items = _all.Where(Matches).ToList();
        var found = keep is null ? -1 : _items.IndexOf(keep);
        _index = found >= 0 ? found : Math.Clamp(_index, 0, Math.Max(0, _items.Count - 1));
        RaiseListState();
    }

    private bool Matches(SorterItem item) => Filter switch
    {
        FilterVideos => item.Kind == SorterItemKind.Video,
        FilterImages => item.Kind == SorterItemKind.Image,
        FilterOther => item.Kind == SorterItemKind.File,
        _ => true
    };

    private void Move(int delta, string? status)
    {
        if (_items.Count == 0) return;
        var target = _index + delta;
        if (target < 0)
        {
            SetStatus("Erste Datei erreicht.");
            return;
        }
        if (target >= _items.Count)
        {
            SetStatus("Letzte Datei erreicht.");
            return;
        }
        _index = target;
        ShowCurrent();
        if (status is not null) SetStatus(status);
    }

    /// <summary>Legt die aktuelle Datei im gewählten Bereich ab und springt weiter.</summary>
    private async Task SortAsync(SorterSlot? slot)
    {
        if (slot is null || Current is not { } item || _busy) return;
        if (slot.Folder.Length == 0)
        {
            SetStatus($"Für „{slot.Name}“ ist kein Ordner hinterlegt.", isError: true);
            return;
        }

        _busy = true;
        OnPropertyChanged(nameof(IsIdle));
        SortCommand.RaiseCanExecuteChanged();
        ReleaseRequested?.Invoke();
        PreviewImage = null;
        try
        {
            var copy = CopyMode;
            var backupRoot = BackupEnabled && item.Kind is SorterItemKind.Image or SorterItemKind.Video
                ? BackupDirectory : null;
            string? resolvedBackupFolder = null;
            if (backupRoot is not null)
            {
                if (string.IsNullOrWhiteSpace(backupRoot)) throw new IOException("Bitte zuerst einen Backup-Ordner wählen oder Backup ausschalten.");
                SetStatus("Backup-Zuordnung prüfen …");
                resolvedBackupFolder = await ResolveBackupFolderAsync(slot.Folder);
            }
            var destination = await SorterService.PlaceAsync(item, slot.Target, copy,
                SettingsService.Current.SorterPreserveSubfolders);

            var entry = new SorterProgressEntry
            {
                RelativePath = item.RelativePath,
                SourcePath = item.FullPath,
                Target = slot.Name,
                Destination = destination,
                Copied = copy
            };
            _progress.Source = SourceDirectory;
            _progress.Entries.Add(entry);
            SorterService.SaveProgress(_progress);

            string? backupError = null;
            if (backupRoot is not null)
            {
                SetStatus("Datei einsortiert — Backup wird kopiert …");
                try { await SorterService.BackupToFolderAsync(destination, slot.Folder, resolvedBackupFolder!); }
                catch (Exception ex) { backupError = ex.Message; }
            }

            _undo.Add(new UndoStep(item, _index, entry, slot));
            if (_undo.Count > 50) _undo.RemoveAt(0);
            foreach (var other in Targets) other.IsLast = false;
            slot.IsLast = true;
            slot.Count++;
            slot.RefreshFolderState();

            _all.Remove(item);
            _items.RemoveAt(_index);
            if (_index >= _items.Count) _index = Math.Max(0, _items.Count - 1);

            var renamed = !string.Equals(Path.GetFileName(destination), item.Name, StringComparison.Ordinal);
            SetStatus(renamed
                ? $"{item.Name} → {slot.Name} (als {Path.GetFileName(destination)} abgelegt)"
                : $"{item.Name} → {slot.Name}");
            if (backupError is not null)
                SetStatus($"Datei einsortiert, aber Backup fehlgeschlagen: {backupError} Rückgängig und erneut einsortieren, um es erneut zu versuchen.", isError: true);
            else if (backupRoot is not null) SetStatus($"{Status} · Backup gespeichert");
            RaiseListState();
            ShowCurrent();
        }
        catch (Exception ex)
        {
            SetStatus($"Konnte nicht abgelegt werden: {ex.Message}", isError: true);
            ShowCurrent();
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(IsIdle));
            SortCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task UndoAsync()
    {
        if (_undo.Count == 0 || _busy) return;
        var step = _undo[^1];
        _busy = true;
        OnPropertyChanged(nameof(IsIdle));
        SortCommand.RaiseCanExecuteChanged();
        ReleaseRequested?.Invoke();
        PreviewImage = null;
        try
        {
            await SorterService.UndoAsync(step.Entry);
            _undo.RemoveAt(_undo.Count - 1);
            _progress.Entries.Remove(step.Entry);
            SorterService.SaveProgress(_progress);
            if (step.Slot.Count > 0) step.Slot.Count--;

            _all.Add(step.Item);
            _all.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            Rebuild(step.Item);
            ShowCurrent();
            SetStatus($"Zurückgenommen: {step.Item.Name}");
        }
        catch (Exception ex)
        {
            SetStatus($"Rückgängig fehlgeschlagen: {ex.Message}", isError: true);
            ShowCurrent();
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(IsIdle));
            SortCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Lädt Vorschau bzw. Player für die aktuelle Datei; verschwundene Dateien fliegen aus der Liste.</summary>
    private void ShowCurrent()
    {
        PreviewImage = null;
        var item = Current;
        while (item is not null && !File.Exists(item.FullPath))
        {
            _all.Remove(item);
            _items.RemoveAt(_index);
            if (_index >= _items.Count) _index = Math.Max(0, _items.Count - 1);
            item = Current;
        }

        if (item is { Kind: SorterItemKind.Image }) PreviewImage = LoadImage(item.FullPath);
        RaiseListState();
        CurrentChanged?.Invoke(item);
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static string KindText(SorterItemKind kind) => kind switch
    {
        SorterItemKind.Video => "Video",
        SorterItemKind.Image => "Bild",
        _ => "Datei"
    };

    private static void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* Explorer nicht verfügbar: still übergehen. */ }
    }

    private void SetStatus(string text, bool isError = false)
    {
        Status = text;
        StatusIsError = isError;
    }

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(CounterText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(SortedCount));
        OnPropertyChanged(nameof(RemainingCount));
        RaiseCurrentState();
    }

    private void RaiseCurrentState()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsPlainFile));
        OnPropertyChanged(nameof(CurrentName));
        OnPropertyChanged(nameof(CurrentMeta));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CurrentBadge));
        OnPropertyChanged(nameof(CurrentSubfolder));
        OnPropertyChanged(nameof(CounterText));
        RaiseListCommands();
    }

    private void RaiseListCommands()
    {
        SortCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
    }
}
