using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;
using Microsoft.Win32;

namespace MediaForge.ViewModels;

/// <summary>
/// Die Bibliothek über den Werkzeugen: registrierte Ordner, Zeitleiste, Suche, Favoriten, Alben und Tags.
/// Sie liest Ordner und legt die Ordnung in einer eigenen Datei ab — Mediendateien bleiben unangetastet.
/// </summary>
public sealed class GalleryViewModel : ObservableObject, IToolAware
{
    public const string FilterAll = "Alle Arten";
    public const string FilterImages = "Nur Fotos";
    public const string FilterVideos = "Nur Videos";

    public const string SortNewest = "Neueste zuerst";
    public const string SortOldest = "Älteste zuerst";
    public const string SortName = "Name A–Z";
    public const string SortNameReverse = "Name Z–A";
    public const string SortKind = "Dateiart";
    public const string SortDuration = "Länge";
    public const string SortSize = "Speichergröße";

    /// <summary>So viele Einträge werden auf einmal in die Zeitleiste gehängt; der Rest folgt beim Scrollen.</summary>
    private const int PageSize = 150;

    private readonly FfmpegService _ffmpeg;
    private readonly Action _back;
    private readonly SemaphoreSlim _thumbnailGate = new(3);

    private GalleryDocument _document = new();
    private Dictionary<string, GalleryEntryRecord> _entries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, GalleryProbeRecord> _probes = new(StringComparer.OrdinalIgnoreCase);

    private List<GalleryItem> _all = [];
    private List<GalleryItem> _filtered = [];
    private readonly HashSet<GalleryItem> _selection = [];
    private CancellationTokenSource? _scanCts;

    private bool _loaded;
    private int _shown;
    private bool _isBusy;
    private string _status = "Noch kein Ordner gewählt.";
    private string _searchText = string.Empty;
    private string _filter = FilterAll;
    private string _sort = SortNewest;
    private bool _durationsRequested;
    private string _tagInput = string.Empty;
    private string _newAlbumName = string.Empty;
    private string _newTagName = string.Empty;
    private string _itemTagName = string.Empty;
    private readonly SortedSet<string> _knownTags = new(StringComparer.Ordinal);
    private GallerySection _section = GallerySection.All;
    private GalleryAlbum? _openAlbum;
    private GalleryTag? _openTag;
    private GalleryFolder? _openFolder;
    private GalleryItem? _current;
    private bool _isViewerOpen;
    private bool _hasChecked;
    private ImageSource? _viewerImage;
    private Uri? _viewerVideo;

    public GalleryViewModel(FfmpegService ffmpeg, Action back)
    {
        _ffmpeg = ffmpeg;
        _back = back;

        BackCommand = new RelayCommand(_ => _back());
        AddRootCommand = new AsyncRelayCommand(_ => AddRootAsync());
        RemoveRootCommand = new AsyncRelayCommand(p => RemoveRootAsync(p as GalleryRoot));
        RescanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => Roots.Count > 0);
        ClearThumbnailsCommand = new AsyncRelayCommand(_ => ClearThumbnailsAsync(), _ => !IsBusy);
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        CheckCommand = new AsyncRelayCommand(_ => CheckAsync(), _ => !IsBusy && _all.Count > 0);

        ShowAllCommand = new RelayCommand(_ => ShowKind(FilterAll));
        ShowPhotosCommand = new RelayCommand(_ => ShowKind(FilterImages));
        ShowVideosCommand = new RelayCommand(_ => ShowKind(FilterVideos));
        ShowFavoritesCommand = new RelayCommand(_ => Open(GallerySection.Favorites));
        ShowNotesCommand = new RelayCommand(_ => Open(GallerySection.Notes));
        OpenAlbumCommand = new RelayCommand(p => OpenAlbum(p as GalleryAlbum));
        OpenTagCommand = new RelayCommand(p => OpenTag(p as GalleryTag));
        OpenRootCommand = new RelayCommand(p => OpenFolder(p as GalleryRoot is { } root
            ? new GalleryFolder(root.Path, string.Empty, root.Count)
            : p as GalleryFolder));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as GalleryFolder));
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
        FillAlbumCommand = new RelayCommand(_ => FillAlbum(), _ => OpenAlbumEntry is not null);
        CreateTagCommand = new RelayCommand(_ => CreateTag(), _ => GalleryTags.Parse(NewTagName).Count > 0);
        RenameAlbumCommand = new RelayCommand(p => StartRename(p as GalleryAlbum));
        RenameTagCommand = new RelayCommand(p => StartRename(p as GalleryTag));
        DeleteTagCommand = new RelayCommand(p => DeleteTag(p as GalleryTag));
        AddItemTagCommand = new RelayCommand(_ => AddItemTag(),
            _ => Current is not null && GalleryTags.Parse(ItemTagName).Count > 0);

        SelectItemCommand = new RelayCommand(p => Current = p as GalleryItem);
        ToggleSelectionCommand = new RelayCommand(p => ToggleSelection(p as GalleryItem));
        SelectAllCommand = new RelayCommand(_ => SelectAll(), _ => _filtered.Count > 0);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => _selection.Count > 0);

        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as GalleryItem ?? Current));
        FavoriteSelectionCommand = new RelayCommand(_ => FavoriteSelection(true), _ => _selection.Count > 0);
        UnfavoriteSelectionCommand = new RelayCommand(_ => FavoriteSelection(false), _ => _selection.Count > 0);

        CreateAlbumCommand = new RelayCommand(_ => CreateAlbum(), _ => NewAlbumName.Trim().Length > 0);
        DeleteAlbumCommand = new RelayCommand(p => DeleteAlbum(p as GalleryAlbum ?? OpenAlbumEntry));
        AddSelectionToAlbumCommand = new RelayCommand(_ => ApplySelectionToAlbums(add: true), _ => CanApplyAlbums);
        RemoveSelectionFromAlbumCommand = new RelayCommand(_ => ApplySelectionToAlbums(add: false), _ => CanApplyAlbums);
        ClearAlbumChoiceCommand = new RelayCommand(_ => ClearAlbumChoice(), _ => Albums.Any(album => album.IsChecked));

        TagSelectionCommand = new RelayCommand(_ => TagSelection(add: true), _ => CanTagSelection);
        UntagSelectionCommand = new RelayCommand(_ => TagSelection(add: false), _ => CanTagSelection);

        OpenViewerCommand = new RelayCommand(p => OpenViewer(p as GalleryItem ?? Current));
        CloseViewerCommand = new RelayCommand(_ => IsViewerOpen = false);
        ViewerNextCommand = new RelayCommand(_ => Step(1), _ => IsViewerOpen);
        ViewerPreviousCommand = new RelayCommand(_ => Step(-1), _ => IsViewerOpen);

        RevealCommand = new RelayCommand(p => Reveal(p as GalleryItem ?? Current));
        OpenExternalCommand = new RelayCommand(p => OpenExternal(p as GalleryItem ?? Current));
    }

    // ------------------------------------------------------------ Befehle

    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand AddRootCommand { get; }
    public AsyncRelayCommand RemoveRootCommand { get; }
    public AsyncRelayCommand RescanCommand { get; }
    public AsyncRelayCommand ClearThumbnailsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ShowPhotosCommand { get; }
    public RelayCommand ShowVideosCommand { get; }
    public RelayCommand ShowFavoritesCommand { get; }
    public RelayCommand ShowNotesCommand { get; }
    public RelayCommand OpenAlbumCommand { get; }
    public RelayCommand OpenTagCommand { get; }
    public RelayCommand OpenRootCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand FillAlbumCommand { get; }
    public RelayCommand CreateTagCommand { get; }
    public RelayCommand RenameAlbumCommand { get; }
    public RelayCommand RenameTagCommand { get; }
    public RelayCommand DeleteTagCommand { get; }
    public RelayCommand AddItemTagCommand { get; }
    public RelayCommand SelectItemCommand { get; }
    public RelayCommand ToggleSelectionCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand FavoriteSelectionCommand { get; }
    public RelayCommand UnfavoriteSelectionCommand { get; }
    public RelayCommand CreateAlbumCommand { get; }
    public RelayCommand DeleteAlbumCommand { get; }
    public RelayCommand AddSelectionToAlbumCommand { get; }
    public RelayCommand RemoveSelectionFromAlbumCommand { get; }
    public RelayCommand ClearAlbumChoiceCommand { get; }
    public RelayCommand TagSelectionCommand { get; }
    public RelayCommand UntagSelectionCommand { get; }
    public RelayCommand OpenViewerCommand { get; }
    public RelayCommand CloseViewerCommand { get; }
    public RelayCommand ViewerNextCommand { get; }
    public RelayCommand ViewerPreviousCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenExternalCommand { get; }

    // ------------------------------------------------------------ Zustand

    public ObservableCollection<GalleryRoot> Roots { get; } = [];
    public ObservableCollection<GalleryAlbum> Albums { get; } = [];

    /// <summary>Was die Seitenleiste zeigt: bei gewähltem Ordner nur die Alben, die dort etwas haben.</summary>
    public ObservableCollection<GalleryAlbum> VisibleAlbums { get; } = [];
    public ObservableCollection<GalleryTag> TagList { get; } = [];
    public ObservableCollection<GalleryDay> Days { get; } = [];
    public string[] Filters { get; } = [FilterAll, FilterImages, FilterVideos];
    public string[] Sorts { get; } =
        [SortNewest, SortOldest, SortName, SortNameReverse, SortKind, SortDuration, SortSize];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RescanCommand.RaiseCanExecuteChanged();
            ClearThumbnailsCommand.RaiseCanExecuteChanged();
            CheckCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Ordner der Bibliotheksdatei — steht als Hinweis am Button, der ihn im Explorer öffnet.</summary>
    public string DataFolder => GalleryStore.Directory;

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(HasSearch)); Rebuild(); } }
    }

    public bool HasSearch => SearchText.Trim().Length > 0;

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            OnPropertyChanged(nameof(NavKey));
            Rebuild();
        }
    }

    /// <summary>Sortierung; gilt in jeder Sicht, auch in Alben, Tags und Ordnern.</summary>
    public string Sort
    {
        get => _sort;
        set
        {
            if (!SetProperty(ref _sort, value)) return;
            // Nach Länge lässt sich erst ordnen, wenn jedes Video einmal befragt wurde.
            if (value == SortDuration) _ = EnsureDurationsAsync();
            Rebuild();
        }
    }

    /// <summary>Eingabefeld für Tags — bedient sowohl den einzelnen Eintrag als auch die Auswahl.</summary>
    public string TagInput
    {
        get => _tagInput;
        set
        {
            if (!SetProperty(ref _tagInput, value)) return;
            TagSelectionCommand.RaiseCanExecuteChanged();
            UntagSelectionCommand.RaiseCanExecuteChanged();
        }
    }

    public string NewAlbumName
    {
        get => _newAlbumName;
        set { if (SetProperty(ref _newAlbumName, value)) CreateAlbumCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Neuer Tag in der Seitenleiste — er steht danach im Menü jedes Eintrags zur Wahl.</summary>
    public string NewTagName
    {
        get => _newTagName;
        set { if (SetProperty(ref _newTagName, value)) CreateTagCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Neuer Tag direkt am angesehenen Eintrag.</summary>
    public string ItemTagName
    {
        get => _itemTagName;
        set { if (SetProperty(ref _itemTagName, value)) AddItemTagCommand.RaiseCanExecuteChanged(); }
    }

    public GallerySection Section
    {
        get => _section;
        private set
        {
            if (!SetProperty(ref _section, value)) return;
            OnPropertyChanged(nameof(IsAlbumOpen));
            OnPropertyChanged(nameof(IsNotes));
            OnPropertyChanged(nameof(NavKey));
            OnPropertyChanged(nameof(CanAddRootHere));
            FillAlbumCommand.RaiseCanExecuteChanged();
            RemoveSelectionFromAlbumCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAlbumOpen => Section == GallerySection.Album;

    /// <summary>Der Hinweisbereich zeigt, was der Bibliothek fehlt oder doppelt in ihr liegt.</summary>
    public bool IsNotes => Section == GallerySection.Notes;

    /// <summary>Dateigruppen mit gleichem Inhalt — die erste Datei ist der älteste Stand.</summary>
    public ObservableCollection<GalleryDuplicateGroup> Duplicates { get; } = [];

    /// <summary>Dateien, die sich nicht ansehen lassen — beschädigt, leer oder im falschen Codec.</summary>
    public ObservableCollection<GalleryIssue> Broken { get; } = [];

    /// <summary>Wurde in dieser Sitzung schon geprüft? Vorher steht im Hinweisbereich nur die Einladung dazu.</summary>
    public bool HasChecked { get => _hasChecked; private set => SetProperty(ref _hasChecked, value); }

    public bool HasDuplicates => Duplicates.Count > 0;
    public bool HasBroken => Broken.Count > 0;

    /// <summary>Geprüft und nichts gefunden — dann bekommt der Bereich eine Entwarnung statt einer Liste.</summary>
    public bool IsClean => HasChecked && !HasDuplicates && !HasBroken;

    /// <summary>Zahl neben „Hinweise“ in der Seitenleiste; ungeprüft bleibt sie leer.</summary>
    public string NotesCountText => HasChecked ? (Duplicates.Sum(group => group.Items.Count - 1) + Broken.Count).ToString() : string.Empty;

    public string DuplicateHeader =>
        $"Duplikate · {Plural(Duplicates.Count, "Gruppe", "Gruppen")} · {Formatters.Size(Duplicates.Sum(group => group.Wasted))} doppelt";

    public string BrokenHeader => "Fehlerhafte Dateien · " + Plural(Broken.Count, "Eintrag", "Einträge");

    /// <summary>Ohne ffprobe lässt sich über Videos nichts sagen — das gehört in den Bereich geschrieben.</summary>
    public bool CanProbeVideos => _ffmpeg.FfprobePath is not null;

    /// <summary>
    /// Im leeren Zustand passt nicht überall dasselbe Angebot: Ordner aufnehmen hilft nur dort,
    /// wo der Inhalt aus Ordnern kommt. Ein Album will gefüllt werden, Favoriten wollen gar nichts.
    /// </summary>
    public bool CanAddRootHere => Section is GallerySection.All or GallerySection.Folder;

    public GalleryAlbum? OpenAlbumEntry { get => _openAlbum; private set => SetProperty(ref _openAlbum, value); }

    /// <summary>Die Alben des gerade angesehenen Eintrags — Häkchen gesetzt heißt: liegt darin.</summary>
    public ObservableCollection<GalleryMembership> CurrentAlbums { get; } = [];

    /// <summary>Die Tags des gerade angesehenen Eintrags — Häkchen gesetzt heißt: er trägt ihn.</summary>
    public ObservableCollection<GalleryTagMembership> CurrentTags { get; } = [];

    /// <summary>Was am zugeklappten Tagmenü steht.</summary>
    public string CurrentTagsText => CurrentTags.Count(entry => entry.IsMember) switch
    {
        0 => "Keine Tags",
        1 => "1 Tag",
        var count => $"{count} Tags"
    };

    /// <summary>Was am zugeklappten Albummenü steht.</summary>
    public string CurrentAlbumsText => CurrentAlbums.Count(entry => entry.IsMember) switch
    {
        0 => "In keinem Album",
        1 => "In 1 Album",
        var count => $"In {count} Alben"
    };

    private bool CanApplyAlbums => _selection.Count > 0 && Albums.Any(album => album.IsChecked);

    public GalleryItem? Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value)) return;
            OnPropertyChanged(nameof(HasCurrent));
            ItemTagName = string.Empty;
            RefreshCurrentAlbums();
            RefreshCurrentTags();
            AddItemTagCommand.RaiseCanExecuteChanged();
            if (IsViewerOpen) _ = LoadViewerAsync();
        }
    }

    public bool HasCurrent => Current is not null;

    public bool IsViewerOpen
    {
        get => _isViewerOpen;
        set
        {
            if (!SetProperty(ref _isViewerOpen, value)) return;
            ViewerNextCommand.RaiseCanExecuteChanged();
            ViewerPreviousCommand.RaiseCanExecuteChanged();
            if (value) _ = LoadViewerAsync();
            else { ViewerImage = null; ViewerVideo = null; }
        }
    }

    public ImageSource? ViewerImage { get => _viewerImage; private set => SetProperty(ref _viewerImage, value); }

    public Uri? ViewerVideo
    {
        get => _viewerVideo;
        private set { if (SetProperty(ref _viewerVideo, value)) OnPropertyChanged(nameof(IsViewerVideo)); }
    }

    public bool IsViewerVideo => ViewerVideo is not null;

    public int SelectionCount => _selection.Count;
    public bool HasSelection => _selection.Count > 0;
    public string SelectionText => _selection.Count == 1 ? "1 ausgewählt" : $"{_selection.Count} ausgewählt";

    /// <summary>Ist gerade ein Ordner gewählt, auf den sich Tags und Alben beziehen?</summary>
    public bool HasScope => _openFolder is not null;

    public string ScopeText => _openFolder is { } folder ? folder.Name : string.Empty;

    /// <summary>Wie viel Platz die Bilder und Videos der aufgenommenen Ordner belegen.</summary>
    public string StorageText
    {
        get
        {
            if (_all.Count == 0) return string.Empty;
            var bytes = _all.Sum(item => item.Size);
            var ordner = _document.Roots.Count;
            return $"{Formatters.Size(bytes)} in {ordner} {(ordner == 1 ? "Ordner" : "Ordnern")}";
        }
    }

    public bool HasRoots => Roots.Count > 0;
    public bool HasItems => _filtered.Count > 0;
    public bool HasMore => _shown < _filtered.Count;
    public bool HasFfmpeg => _ffmpeg.IsAvailable;

    /// <summary>Überschrift der offenen Sicht.</summary>
    public string Headline => Section switch
    {
        GallerySection.Favorites => "Favoriten",
        GallerySection.Notes => "Hinweise",
        GallerySection.Album => OpenAlbumEntry?.Name ?? "Album",
        GallerySection.Tag => "#" + (_openTag?.Name ?? string.Empty),
        GallerySection.Folder => _openFolder?.Name ?? "Ordner",
        _ => Filter switch
        {
            FilterImages => "Fotos",
            FilterVideos => "Videos",
            _ => "Galerie"
        }
    };

    /// <summary>
    /// Welcher Eintrag der Seitenleiste hervorgehoben wird. Fotos und Videos sind keine eigenen Bereiche,
    /// sondern die Artfilter — deshalb greift die Hervorhebung auch, wenn oben rechts umgestellt wird.
    /// </summary>
    public string NavKey => Section switch
    {
        GallerySection.Favorites => "favorites",
        GallerySection.Notes => "notes",
        GallerySection.All => Filter switch
        {
            FilterImages => "photos",
            FilterVideos => "videos",
            _ => "all"
        },
        _ => string.Empty
    };

    public string Subline
    {
        get
        {
            if (IsNotes)
            {
                if (!HasChecked) return "Doppelte und fehlerhafte Dateien aufspüren.";
                var copies = Duplicates.Sum(group => group.Items.Count - 1);
                return copies == 0 && Broken.Count == 0
                    ? "Geprüft — nichts gefunden."
                    : $"{Plural(copies, "Kopie", "Kopien")} · {Plural(Broken.Count, "fehlerhafte Datei", "fehlerhafte Dateien")}";
            }
            if (_all.Count == 0) return HasRoots ? "Keine Bilder oder Videos gefunden." : "Wähle links einen Ordner aus.";
            var images = _filtered.Count(item => item.Kind == GalleryKind.Image);
            var videos = _filtered.Count - images;
            var shown = HasMore ? $"{_shown} von {_filtered.Count} geladen · " : string.Empty;
            var ordner = _openFolder is { } folder && Section != GallerySection.Folder
                ? $"in „{folder.Name}“ · "
                : string.Empty;
            return $"{ordner}{shown}{Plural(_filtered.Count, "Eintrag", "Einträge")} · " +
                   $"{Plural(images, "Foto", "Fotos")} · {Plural(videos, "Video", "Videos")}";
        }
    }

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private bool CanTagSelection => _selection.Count > 0 && GalleryTags.Parse(TagInput).Count > 0;

    public void RefreshTools() => OnPropertyChanged(nameof(HasFfmpeg));

    // ------------------------------------------------------------ Laden und Einlesen

    /// <summary>Wird beim ersten Öffnen der Ansicht gerufen: Bibliothek laden und Ordner einlesen.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;

        _document = GalleryStore.Load();
        _entries = GalleryStore.EntryMap(_document);
        foreach (var tag in GalleryTags.Normalize(_document.Tags)) _knownTags.Add(tag);
        foreach (var entry in _entries.Values)
            foreach (var tag in entry.Tags) _knownTags.Add(tag);
        _probes = GalleryStore.ProbeMap(_document);
        foreach (var album in GalleryStore.ToAlbums(_document)) Track(album);
        RefreshRoots();

        if (_document.Roots.Count > 0) await ScanAsync();
        else Status = "Noch kein Ordner gewählt — links „Ordner hinzufügen“.";
    }

    private async Task AddRootAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Ordner zur Bibliothek hinzufügen" };
        if (dialog.ShowDialog() != true) return;

        var folder = Path.GetFullPath(dialog.FolderName);
        if (_document.Roots.Any(root => string.Equals(Path.GetFullPath(root), folder, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Dieser Ordner ist bereits in der Bibliothek.";
            return;
        }
        _document.Roots.Add(folder);
        Persist();
        await ScanAsync();
    }

    private async Task RemoveRootAsync(GalleryRoot? root)
    {
        if (root is null) return;
        _document.Roots.RemoveAll(entry =>
            string.Equals(entry.TrimEnd(Path.DirectorySeparatorChar), root.Path.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));
        if (_openFolder is { } open &&
            string.Equals(open.RootPath, root.Path, StringComparison.OrdinalIgnoreCase))
            Section = GallerySection.All;
        Persist();
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsBusy = true;
        Status = "Ordner werden eingelesen…";
        try
        {
            var roots = _document.Roots.ToList();
            var items = await Task.Run(() => GalleryService.Scan(roots, token), token);
            if (token.IsCancellationRequested) return;

            foreach (var item in items)
            {
                if (_entries.TryGetValue(item.Key, out var entry))
                {
                    item.IsFavorite = entry.Favorite;
                    item.SetTags(entry.Tags);
                }
                if (item.IsVideo && _probes.TryGetValue(item.Key, out var probe) && probe.ModifiedTicks == item.ModifiedTicks)
                    item.DurationMs = probe.DurationMs;
            }

            _all = items;
            // Ein neuer Stand der Ordner macht die alten Funde ungueltig — lieber leer als falsch.
            Duplicates.Clear();
            Broken.Clear();
            HasChecked = false;
            RaiseNotes();
            var moved = Relink(items);
            RefreshRoots();
            RefreshScopedLists();
            RefreshCurrentTags();
            OnPropertyChanged(nameof(StorageText));
            foreach (var album in Albums) album.RefreshCount();
            Rebuild();
            var wiedererkannt = moved == 0
                ? string.Empty
                : moved == 1
                    ? " · 1 verschobene Datei wiedererkannt"
                    : $" · {moved} verschobene Dateien wiedererkannt";
            Status = _all.Count == 0
                ? "Keine Bilder oder Videos in den gewählten Ordnern."
                : $"{_all.Count} Einträge aus {_document.Roots.Count} Ordner(n){wiedererkannt}.";
        }
        catch (OperationCanceledException) { /* Ein neuer Scan hat übernommen. */ }
        finally { IsBusy = false; }
    }

    private async Task ClearThumbnailsAsync()
    {
        var freed = await Task.Run(GalleryService.ClearThumbnails);
        foreach (var item in _all) { item.Thumbnail = null; item.ThumbnailRequested = false; }
        Status = $"Vorschaubilder gelöscht ({Formatters.Size(freed)} frei).";
        Rebuild();
    }

    /// <summary>
    /// Hängt die Ordnung von Dateien, die nicht mehr am gemerkten Pfad liegen, an ihren neuen Platz.
    /// Erkannt wird über Dateiname und Größe — ein Verschieben in einen anderen Unterordner ändert beides nicht.
    /// Mehrdeutige Fälle bleiben unangetastet, lieber nichts zuordnen als das Falsche.
    /// </summary>
    private int Relink(List<GalleryItem> items)
    {
        var known = new HashSet<string>(items.Select(item => item.Key), StringComparer.OrdinalIgnoreCase);
        var orphans = _entries.Values.Where(entry => !known.Contains(entry.Path)).ToList();
        if (orphans.Count == 0) return 0;

        // Nur Dateien, die noch keine eigene Ordnung tragen, kommen als neuer Platz in Frage.
        var candidates = items
            .Where(item => !_entries.ContainsKey(item.Key))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var moved = 0;
        foreach (var orphan in orphans)
        {
            var name = orphan.Name.Length > 0 ? orphan.Name : Path.GetFileName(orphan.Path);
            if (name.Length == 0 || !candidates.TryGetValue(name, out var matches) || matches.Count == 0) continue;

            GalleryItem? target;
            if (orphan.Size > 0)
            {
                var sized = matches.Where(item => item.Size == orphan.Size).ToList();
                target = sized.Count == 1 ? sized[0] : null;
            }
            else target = matches.Count == 1 ? matches[0] : null;
            if (target is null) continue;

            var previous = orphan.Path;
            _entries.Remove(previous);
            orphan.Path = target.Key;
            orphan.Name = target.Name;
            orphan.Size = target.Size;
            _entries[target.Key] = orphan;

            target.IsFavorite = orphan.Favorite;
            target.SetTags(orphan.Tags);
            foreach (var album in Albums)
                if (album.Keys.Remove(previous)) album.Keys.Add(target.Key);
            if (_probes.Remove(previous, out var probe))
            {
                probe.Path = target.Key;
                _probes[target.Key] = probe;
                _dirtyProbes = true;
            }

            matches.Remove(target);
            moved++;
        }

        if (moved > 0) Persist();
        return moved;
    }

    private void RefreshRoots()
    {
        Roots.Clear();
        foreach (var path in _document.Roots)
        {
            var owned = _all.Where(item => string.Equals(item.RootPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            var root = new GalleryRoot(path, owned.Count);

            foreach (var branch in Branches(path, owned, string.Empty)) root.Subfolders.Add(branch);
            Roots.Add(root);
        }
        OnPropertyChanged(nameof(HasRoots));
        RescanCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Baut den Ordnerbaum einer Ebene und rekursiv alles darunter. Die Anzahl eines Ordners
    /// schließt seine Unterordner mit ein — sonst wäre ein zugeklappter Ast still leer.
    /// </summary>
    private static List<GalleryFolder> Branches(string rootPath, IReadOnlyList<GalleryItem> items, string prefix)
    {
        var depth = prefix.Length == 0 ? 0 : prefix.Split(Separators).Length;
        var folders = new List<GalleryFolder>();

        var groups = items
            .Select(item => (item, parts: item.Subfolder.Length == 0 ? [] : item.Subfolder.Split(Separators)))
            .Where(entry => entry.parts.Length > depth)
            .GroupBy(entry => entry.parts[depth], StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in groups)
        {
            var relative = prefix.Length == 0 ? group.Key : Path.Combine(prefix, group.Key);
            var owned = group.Select(entry => entry.item).ToList();
            var folder = new GalleryFolder(rootPath, relative, owned.Count);
            foreach (var child in Branches(rootPath, owned, relative)) folder.Children.Add(child);
            folders.Add(folder);
        }
        return folders;
    }

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Die Einträge, auf die sich die Seitenleiste bezieht — der gewählte Ordner oder alles.</summary>
    private List<GalleryItem> InScope() =>
        _openFolder is { } folder ? [.. _all.Where(folder.Contains)] : _all;

    private void RefreshScopedLists()
    {
        RefreshTagList();
        RefreshVisibleAlbums();
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(HasScope));
    }

    private void RefreshTagList()
    {
        var scope = InScope();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // Ohne Ordnerauswahl steht auch der angelegte, aber noch nicht vergebene Vorrat in der Liste.
        if (_openFolder is null) foreach (var tag in _knownTags) counts[tag] = 0;
        foreach (var item in scope)
            foreach (var tag in item.Tags)
                counts[tag] = counts.GetValueOrDefault(tag) + 1;

        TagList.Clear();
        foreach (var pair in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            TagList.Add(new GalleryTag(pair.Key, pair.Value));
    }

    /// <summary>Im Ordner erscheinen nur Alben, die dort etwas liegen haben — mit der Anzahl von dort.</summary>
    private void RefreshVisibleAlbums()
    {
        var scope = InScope();
        VisibleAlbums.Clear();
        foreach (var album in Albums)
        {
            if (_openFolder is null)
            {
                album.ScopedCount = album.Keys.Count;
                VisibleAlbums.Add(album);
                continue;
            }
            var count = scope.Count(item => album.Keys.Contains(item.Key));
            if (count == 0) continue;
            album.ScopedCount = count;
            VisibleAlbums.Add(album);
        }
    }

    /// <summary>Legt einen Tag an, ohne ihn schon zu vergeben — er steht danach in jedem Menü zur Wahl.</summary>
    private void CreateTag()
    {
        var neu = GalleryTags.Parse(NewTagName);
        if (neu.Count == 0) return;
        foreach (var tag in neu) _knownTags.Add(tag);
        NewTagName = string.Empty;
        Persist();
        RefreshScopedLists();
        RefreshCurrentTags();
        Status = neu.Count == 1 ? $"Tag „#{neu[0]}“ angelegt." : $"{neu.Count} Tags angelegt.";
    }

    /// <summary>Hängt den eingetippten Tag an den angesehenen Eintrag.</summary>
    private void AddItemTag()
    {
        if (Current is not { } item) return;
        var neu = GalleryTags.Parse(ItemTagName);
        if (neu.Count == 0) return;
        foreach (var tag in neu) _knownTags.Add(tag);
        var ergebnis = GalleryTags.Normalize(item.Tags.Concat(neu));
        item.SetTags(ergebnis);
        Entry(item).Tags = ergebnis;
        ItemTagName = string.Empty;
        Persist();
        RefreshScopedLists();
        RefreshCurrentTags();
        if (Section == GallerySection.Tag) Rebuild();
    }

    // ------------------------------------------------------------ Umbenennen und Entfernen

    /// <summary>Schaltet die Zeile in der Seitenleiste auf Eingabe um.</summary>
    private static void StartRename(GalleryAlbum? album)
    {
        if (album is null) return;
        album.EditName = album.Name;
        album.IsRenaming = true;
    }

    private static void StartRename(GalleryTag? tag)
    {
        if (tag is null) return;
        tag.EditName = tag.Name;
        tag.IsRenaming = true;
    }

    /// <summary>Übernimmt die Eingabe aus der Seitenleiste — von der Ansicht gerufen.</summary>
    public void CommitRename(object? item)
    {
        switch (item)
        {
            case GalleryAlbum album: CommitAlbumRename(album); break;
            case GalleryTag tag: CommitTagRename(tag); break;
        }
    }

    /// <summary>Verwirft die Eingabe — von der Ansicht gerufen.</summary>
    public void CancelRename(object? item)
    {
        switch (item)
        {
            case GalleryAlbum album: album.IsRenaming = false; break;
            case GalleryTag tag: tag.IsRenaming = false; break;
        }
    }

    private void CommitAlbumRename(GalleryAlbum album)
    {
        if (!album.IsRenaming) return;
        album.IsRenaming = false;
        var name = album.EditName.Trim();
        if (name.Length == 0 || name == album.Name) return;
        var alt = album.Name;
        album.Name = name;
        RefreshCurrentAlbums();
        Persist();
        OnPropertyChanged(nameof(Headline));
        Status = $"Album „{alt}“ heißt jetzt „{name}“.";
    }

    /// <summary>
    /// Benennt einen Tag überall um: an den eingelesenen Einträgen, in den gemerkten Einträgen
    /// nicht eingelesener Dateien und im Vorrat. Trifft er auf einen vorhandenen Tag, verschmelzen beide.
    /// </summary>
    private void CommitTagRename(GalleryTag tag)
    {
        if (!tag.IsRenaming) return;
        tag.IsRenaming = false;
        var neu = GalleryTags.Parse(tag.EditName).FirstOrDefault();
        var alt = tag.Name;
        if (neu is null || neu == alt) return;

        foreach (var item in _all.Where(eintrag => eintrag.Tags.Contains(alt, StringComparer.Ordinal)))
        {
            var ergebnis = GalleryTags.Normalize(item.Tags.Where(vorhanden => vorhanden != alt).Append(neu));
            item.SetTags(ergebnis);
            Entry(item).Tags = ergebnis;
        }
        foreach (var entry in _entries.Values.Where(eintrag => eintrag.Tags.Contains(alt, StringComparer.Ordinal)))
            entry.Tags = GalleryTags.Normalize(entry.Tags.Where(vorhanden => vorhanden != alt).Append(neu));

        _knownTags.Remove(alt);
        _knownTags.Add(neu);
        Persist();
        RefreshScopedLists();
        RefreshCurrentTags();
        if (Section == GallerySection.Tag && _openTag?.Name == alt)
        {
            _openTag = TagList.FirstOrDefault(eintrag => eintrag.Name == neu);
            OnPropertyChanged(nameof(Headline));
            Rebuild();
        }
        Status = $"Tag „#{alt}“ heißt jetzt „#{neu}“.";
    }

    /// <summary>Nimmt einen Tag aus dem Vorrat und von allen Einträgen — Dateien bleiben unberührt.</summary>
    private void DeleteTag(GalleryTag? tag)
    {
        if (tag is null) return;
        var name = tag.Name;

        foreach (var item in _all.Where(eintrag => eintrag.Tags.Contains(name, StringComparer.Ordinal)))
        {
            var ergebnis = GalleryTags.Normalize(item.Tags.Where(vorhanden => vorhanden != name));
            item.SetTags(ergebnis);
            Entry(item).Tags = ergebnis;
        }
        foreach (var entry in _entries.Values.Where(eintrag => eintrag.Tags.Contains(name, StringComparer.Ordinal)))
            entry.Tags = GalleryTags.Normalize(entry.Tags.Where(vorhanden => vorhanden != name));

        _knownTags.Remove(name);
        if (Section == GallerySection.Tag && _openTag?.Name == name) Open(GallerySection.All);
        Persist();
        RefreshScopedLists();
        RefreshCurrentTags();
        Status = $"Tag „#{name}“ entfernt — die Dateien bleiben unberührt.";
    }

    /// <summary>Baut die Tagliste des angesehenen Eintrags neu — das Häkchen zeigt die Zugehörigkeit.</summary>
    private void RefreshCurrentTags()
    {
        CurrentTags.Clear();
        if (Current is { } item)
            foreach (var tag in _knownTags)
                CurrentTags.Add(new GalleryTagMembership(tag, item.Tags.Contains(tag, StringComparer.Ordinal),
                    SetTagMembership));
        OnPropertyChanged(nameof(CurrentTagsText));
    }

    /// <summary>Häkchen im Tagmenü: vergibt den Tag oder nimmt ihn weg.</summary>
    private void SetTagMembership(string tag, bool member)
    {
        if (Current is not { } item) return;
        var ergebnis = member
            ? GalleryTags.Normalize(item.Tags.Append(tag))
            : GalleryTags.Normalize(item.Tags.Where(vorhanden => !string.Equals(vorhanden, tag, StringComparison.Ordinal)));
        item.SetTags(ergebnis);
        Entry(item).Tags = ergebnis;
        _knownTags.Add(tag);
        OnPropertyChanged(nameof(CurrentTagsText));
        Persist();
        RefreshScopedLists();
        Status = member ? $"„{item.Name}“ trägt jetzt #{tag}." : $"#{tag} von „{item.Name}“ entfernt.";
        if (Section == GallerySection.Tag) Rebuild();
    }

    // ------------------------------------------------------------ Sichten und Filter

    /// <summary>Die Einträge „Galerie“, „Fotos“ und „Videos“ stellen den Artfilter um.</summary>
    private void ShowKind(string filter)
    {
        Section = GallerySection.All;
        OpenAlbumEntry = null;
        _openTag = null;
        _openFolder = null;
        if (_filter == filter) Rebuild();
        else Filter = filter;
    }

    private void Open(GallerySection section)
    {
        Section = section;
        if (section != GallerySection.Album) OpenAlbumEntry = null;
        if (section != GallerySection.Tag) _openTag = null;
        // Tags und Alben bleiben im gewählten Ordner; nur die obersten Sichten heben ihn auf.
        if (section is GallerySection.All or GallerySection.Favorites or GallerySection.Notes) _openFolder = null;
        RefreshScopedLists();
        Rebuild();
    }

    /// <summary>
    /// Führt aus dem leeren Album in die Galerie und merkt sich das Album als Ziel — ausgewählt
    /// wird dort mit den Häkchen, abgelegt über die Auswahlleiste.
    /// </summary>
    private void FillAlbum()
    {
        if (OpenAlbumEntry is not { } album) return;
        ShowKind(FilterAll);
        foreach (var entry in Albums) entry.IsChecked = entry == album;
        RaiseSelection();
        Status = $"Einträge auswählen und über die Auswahlleiste zu „{album.Name}“ hinzufügen.";
    }

    private void OpenAlbum(GalleryAlbum? album)
    {
        if (album is null) return;
        OpenAlbumEntry = album;
        Open(GallerySection.Album);
    }

    private void OpenTag(GalleryTag? tag)
    {
        if (tag is null) return;
        _openTag = tag;
        Open(GallerySection.Tag);
    }

    private void OpenFolder(GalleryFolder? folder)
    {
        if (folder is null) return;
        _openFolder = folder;
        Open(GallerySection.Folder);
    }

    private void Rebuild()
    {
        _filtered = Apply();
        Days.Clear();
        _shown = 0;
        AppendPage();

        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Subline));
        OnPropertyChanged(nameof(HasItems));
        SelectAllCommand.RaiseCanExecuteChanged();
    }

    private List<GalleryItem> Apply()
    {
        IEnumerable<GalleryItem> query = _all;
        if (_openFolder is { } scope) query = query.Where(scope.Contains);

        query = Section switch
        {
            GallerySection.Favorites => query.Where(item => item.IsFavorite),
            GallerySection.Notes => [],
            GallerySection.Album => OpenAlbumEntry is { } album
                ? query.Where(item => album.Keys.Contains(item.Key))
                : [],
            GallerySection.Tag => _openTag is { } tag
                ? query.Where(item => item.Tags.Contains(tag.Name, StringComparer.Ordinal))
                : [],
            _ => query
        };


        query = Filter switch
        {
            FilterImages => query.Where(item => item.Kind == GalleryKind.Image),
            FilterVideos => query.Where(item => item.Kind == GalleryKind.Video),
            _ => query
        };

        var tokens = SearchText.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in tokens)
        {
            var needle = raw.Trim();
            if (needle.Length == 0) continue;
            if (needle.StartsWith('#'))
            {
                var wanted = needle.TrimStart('#').ToLowerInvariant();
                if (wanted.Length == 0) continue;
                query = query.Where(item => item.Tags.Any(tag => tag.Contains(wanted, StringComparison.Ordinal)));
            }
            else
            {
                var wanted = needle;
                query = query.Where(item =>
                    item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    item.Subfolder.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    item.Tags.Any(tag => tag.Contains(wanted, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return Order(query);
    }

    /// <summary>Bringt die gefilterten Einträge in die gewählte Reihenfolge.</summary>
    private List<GalleryItem> Order(IEnumerable<GalleryItem> query) => Sort switch
    {
        SortOldest => [.. query.OrderBy(item => item.TakenAt)],
        SortName => [.. query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)],
        SortNameReverse => [.. query.OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)],
        SortKind => [.. query
            .OrderBy(item => item.Extension, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)],
        SortDuration => [.. query
            .OrderByDescending(item => item.DurationMs)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)],
        SortSize => [.. query.OrderByDescending(item => item.Size)],
        _ => [.. query.OrderByDescending(item => item.TakenAt)]
    };

    /// <summary>Gruppenschlüssel und Überschrift eines Eintrags — je nach Sortierung.</summary>
    private (string Key, string Header) GroupOf(GalleryItem item)
    {
        switch (Sort)
        {
            case SortName:
            case SortNameReverse:
                var first = item.Name.Length == 0 ? '#' : char.ToUpperInvariant(item.Name[0]);
                if (!char.IsLetter(first)) first = '#';
                return (first.ToString(), first.ToString());
            case SortKind:
                var extension = item.Extension.Length == 0 ? "Ohne Endung" : item.Extension;
                return (extension, extension);
            case SortDuration:
                return ("duration", "Nach Länge — längste zuerst");
            case SortSize:
                return ("size", "Nach Speichergröße — größte zuerst");
            default:
                return (item.Day.ToString("yyyy-MM-dd"), GalleryService.DayHeader(item.Day));
        }
    }

    /// <summary>Hängt die nächste Seite an die Zeitleiste; wird beim Scrollen ans Ende erneut gerufen.</summary>
    public void AppendPage()
    {
        var target = Math.Min(_shown + PageSize, _filtered.Count);
        while (_shown < target)
        {
            var item = _filtered[_shown++];
            var (key, header) = GroupOf(item);
            var day = Days.LastOrDefault();
            if (day is null || day.Key != key)
            {
                day = new GalleryDay { Key = key, Header = header };
                Days.Add(day);
            }
            day.Items.Add(item);
            RequestThumbnail(item);
        }
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(Subline));
    }

    private async void RequestThumbnail(GalleryItem item)
    {
        if (item.ThumbnailRequested || item.Thumbnail is not null) return;
        item.ThumbnailRequested = true;
        await _thumbnailGate.WaitAsync();
        try
        {
            if (item.IsVideo && item.DurationMs == 0 && _ffmpeg.IsAvailable)
            {
                var info = await Task.Run(() => _ffmpeg.ProbeAsync(item.FullPath));
                if (info.Duration > 0)
                {
                    item.DurationMs = Formatters.ToMs(info.Duration);
                    RememberProbe(item);
                }
            }
            var image = await Task.Run(() => GalleryService.ThumbnailAsync(item, _ffmpeg));
            if (image is not null) item.Thumbnail = image;
        }
        catch { /* Ein fehlendes Vorschaubild darf die Ansicht nicht stören. */ }
        finally { _thumbnailGate.Release(); }
    }

    /// <summary>
    /// Befragt einmalig jedes Video, dessen Dauer noch fehlt. Ohne das ließe sich nach Länge nicht
    /// verlässlich ordnen, weil die Dauer sonst erst beim Erzeugen des Vorschaubilds anfällt.
    /// </summary>
    private async Task EnsureDurationsAsync()
    {
        if (_durationsRequested || !_ffmpeg.IsAvailable) return;
        var pending = _all.Where(item => item.IsVideo && item.DurationMs == 0).ToList();
        if (pending.Count == 0) { _durationsRequested = true; return; }

        _durationsRequested = true;
        IsBusy = true;
        Status = $"Länge von {pending.Count} Videos wird ermittelt…";
        try
        {
            var done = 0;
            foreach (var item in pending)
            {
                await _thumbnailGate.WaitAsync();
                try
                {
                    var info = await Task.Run(() => _ffmpeg.ProbeAsync(item.FullPath));
                    if (info.Duration > 0)
                    {
                        item.DurationMs = Formatters.ToMs(info.Duration);
                        RememberProbe(item);
                    }
                }
                catch { /* Ein unlesbares Video bleibt ohne Länge und rutscht ans Ende. */ }
                finally { _thumbnailGate.Release(); }

                if (++done % 25 == 0) Status = $"Länge ermittelt: {done} von {pending.Count}…";
            }
            Persist();
            Status = $"Länge von {done} Videos ermittelt.";
            if (Sort == SortDuration) Rebuild();
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Hält fest, was über ein Video bekannt ist. Ohne <paramref name="info"/> wird nur die Dauer
    /// aufgefrischt; mit ffprobe-Auskunft kommen die Angaben dazu, aus denen die Prüfung ihre
    /// Hinweise ableitet.
    /// </summary>
    private void RememberProbe(GalleryItem item, MediaInfo? info = null)
    {
        if (!_probes.TryGetValue(item.Key, out var probe))
            _probes[item.Key] = probe = new GalleryProbeRecord { Path = item.Key };
        probe.ModifiedTicks = item.ModifiedTicks;
        probe.DurationMs = item.DurationMs;
        if (info is not null)
        {
            probe.VideoCodec = info.VideoCodec;
            probe.Ok = info.Ok;
            probe.HasVideo = info.HasVideo;
        }
        _dirtyProbes = true;
    }

    private bool _dirtyProbes;

    // ------------------------------------------------------------ Hinweise

    /// <summary>
    /// Geht die ganze Bibliothek durch: erst gleich große Dateien auf gleichen Inhalt, dann jede
    /// Datei darauf, ob sie sich öffnen lässt. Videos werden dafür einmal von ffprobe befragt;
    /// das Ergebnis landet in der Bibliotheksdatei, damit die nächste Prüfung schnell ist.
    /// </summary>
    private async Task CheckAsync()
    {
        var items = _all.ToList();
        if (items.Count == 0) { Status = "Nichts zu prüfen — die Bibliothek ist leer."; return; }

        IsBusy = true;
        Duplicates.Clear();
        Broken.Clear();
        RaiseNotes();
        try
        {
            Status = "Suche nach doppelten Dateien…";
            var duplicates = await Task.Run(() => GalleryCheck.FindDuplicates(items));
            foreach (var group in duplicates) Duplicates.Add(group);

            Status = $"Dateien werden geprüft: 0 von {items.Count}…";
            var done = 0;
            foreach (var item in items)
            {
                var problem = GalleryCheck.FileProblem(item);
                if (problem is null)
                {
                    problem = item.IsVideo
                        ? GalleryCheck.VideoProblem(await InspectVideoAsync(item))
                        : await Task.Run(() => GalleryCheck.ImageProblem(item));
                }
                if (problem is not null) Broken.Add(new GalleryIssue { Item = item, Reason = problem, IsFault = true });
                if (++done % 25 == 0) Status = $"Dateien werden geprüft: {done} von {items.Count}…";
            }

            HasChecked = true;
            Persist();
            Status = Summarize(items.Count);
        }
        catch (Exception ex) { Status = "Die Prüfung ist gescheitert: " + ex.Message; }
        finally
        {
            IsBusy = false;
            RaiseNotes();
        }
    }

    private string Summarize(int count)
    {
        var copies = Duplicates.Sum(group => group.Items.Count - 1);
        if (copies == 0 && Broken.Count == 0)
            return $"{count} Dateien geprüft — nichts gefunden.";
        var parts = new List<string>();
        if (copies > 0) parts.Add($"{Plural(copies, "Kopie", "Kopien")} in {Plural(Duplicates.Count, "Gruppe", "Gruppen")}");
        if (Broken.Count > 0) parts.Add($"{Plural(Broken.Count, "fehlerhafte Datei", "fehlerhafte Dateien")}");
        return $"{count} Dateien geprüft — " + string.Join(" · ", parts) + ".";
    }

    /// <summary>
    /// Holt die ffprobe-Auskunft zu einem Video — aus der Bibliotheksdatei, solange die Datei
    /// unverändert ist, sonst frisch. Ohne ffprobe bleibt die Antwort leer, dann entfällt der Hinweis.
    /// </summary>
    private async Task<MediaInfo?> InspectVideoAsync(GalleryItem item)
    {
        if (_probes.TryGetValue(item.Key, out var known) &&
            known.ModifiedTicks == item.ModifiedTicks && known.VideoCodec.Length > 0)
        {
            return new MediaInfo
            {
                Ok = known.Ok,
                HasVideo = known.HasVideo,
                VideoCodec = known.VideoCodec,
                Duration = Formatters.ToSeconds(known.DurationMs)
            };
        }

        if (!CanProbeVideos) return null;

        await _thumbnailGate.WaitAsync();
        try
        {
            var info = await Task.Run(() => _ffmpeg.ProbeAsync(item.FullPath));
            if (info.Duration > 0) item.DurationMs = Formatters.ToMs(info.Duration);
            RememberProbe(item, info);
            return info;
        }
        catch { return null; }
        finally { _thumbnailGate.Release(); }
    }

    private void RaiseNotes()
    {
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(HasBroken));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(NotesCountText));
        OnPropertyChanged(nameof(DuplicateHeader));
        OnPropertyChanged(nameof(BrokenHeader));
        OnPropertyChanged(nameof(Subline));
    }

    // ------------------------------------------------------------ Auswahl

    private void ToggleSelection(GalleryItem? item)
    {
        if (item is null) return;
        if (!_selection.Add(item)) _selection.Remove(item);
        item.IsSelected = _selection.Contains(item);
        RaiseSelection();
    }

    private void SelectAll()
    {
        foreach (var item in _filtered) { _selection.Add(item); item.IsSelected = true; }
        RaiseSelection();
    }

    private void ClearSelection()
    {
        foreach (var item in _selection) item.IsSelected = false;
        _selection.Clear();
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        // Jedes Album zeigt, wie viel der Auswahl schon drin liegt.
        foreach (var album in Albums)
        {
            if (_selection.Count == 0) { album.SelectionHint = string.Empty; continue; }
            var drin = _selection.Count(item => album.Keys.Contains(item.Key));
            album.SelectionHint = drin == 0 ? string.Empty : $"{drin}/{_selection.Count}";
        }

        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        ClearSelectionCommand.RaiseCanExecuteChanged();
        FavoriteSelectionCommand.RaiseCanExecuteChanged();
        UnfavoriteSelectionCommand.RaiseCanExecuteChanged();
        AddSelectionToAlbumCommand.RaiseCanExecuteChanged();
        RemoveSelectionFromAlbumCommand.RaiseCanExecuteChanged();
        ClearAlbumChoiceCommand.RaiseCanExecuteChanged();
        TagSelectionCommand.RaiseCanExecuteChanged();
        UntagSelectionCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------ Favoriten, Alben, Tags

    private void ToggleFavorite(GalleryItem? item)
    {
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        Entry(item).Favorite = item.IsFavorite;
        Persist();
        if (Section == GallerySection.Favorites) Rebuild();
    }

    private void FavoriteSelection(bool favorite)
    {
        foreach (var item in _selection)
        {
            item.IsFavorite = favorite;
            Entry(item).Favorite = favorite;
        }
        Persist();
        Status = favorite ? $"{_selection.Count} als Favorit markiert." : $"{_selection.Count} aus den Favoriten entfernt.";
        if (Section == GallerySection.Favorites) Rebuild();
    }

    private void CreateAlbum()
    {
        var album = new GalleryAlbum { Name = NewAlbumName.Trim() };
        Track(album);
        NewAlbumName = string.Empty;
        RefreshCurrentAlbums();
        Persist();
        Status = $"Album „{album.Name}“ angelegt.";
    }

    private void DeleteAlbum(GalleryAlbum? album)
    {
        if (album is null) return;
        album.PropertyChanged -= Album_PropertyChanged;
        Albums.Remove(album);
        VisibleAlbums.Remove(album);
        if (OpenAlbumEntry == album) { OpenAlbumEntry = null; Open(GallerySection.All); }
        RefreshCurrentAlbums();
        RaiseSelection();
        Persist();
        Status = $"Album „{album.Name}“ gelöscht — die Dateien bleiben unberührt.";
    }

    /// <summary>Legt die Auswahl in alle angehakten Alben — oder nimmt sie dort wieder heraus.</summary>
    private void ApplySelectionToAlbums(bool add)
    {
        var targets = Albums.Where(album => album.IsChecked).ToList();
        if (targets.Count == 0 || _selection.Count == 0) return;

        var touched = 0;
        foreach (var album in targets)
        {
            foreach (var item in _selection)
            {
                if (add)
                {
                    Entry(item);
                    if (album.Keys.Add(item.Key)) touched++;
                }
                else if (album.Keys.Remove(item.Key)) touched++;
            }
            album.RefreshCount();
        }

        Persist();
        RefreshCurrentAlbums();
        RefreshVisibleAlbums();
        RaiseSelection();
        var namen = string.Join(", ", targets.Select(album => album.Name));
        Status = add
            ? $"{touched} Zuordnungen zu {namen} hinzugefügt."
            : $"{touched} Zuordnungen aus {namen} entfernt.";
        if (Section == GallerySection.Album && OpenAlbumEntry is { } offen && targets.Contains(offen)) Rebuild();
    }

    /// <summary>
    /// Nimmt ein Album in die Liste und hört mit, wenn seine Marke an- oder abgehakt wird —
    /// sonst blieben „Ins Album“ und „Aus Album“ grau, bis sich sonst etwas rührt.
    /// </summary>
    private void Track(GalleryAlbum album)
    {
        album.PropertyChanged += Album_PropertyChanged;
        Albums.Add(album);
        RefreshVisibleAlbums();
    }

    private void Album_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GalleryAlbum.IsChecked)) return;
        AddSelectionToAlbumCommand.RaiseCanExecuteChanged();
        RemoveSelectionFromAlbumCommand.RaiseCanExecuteChanged();
        ClearAlbumChoiceCommand.RaiseCanExecuteChanged();
    }

    private void ClearAlbumChoice()
    {
        foreach (var album in Albums) album.IsChecked = false;
        RaiseSelection();
    }

    /// <summary>Baut die Albumliste des angesehenen Eintrags neu — das Häkchen zeigt die Zugehörigkeit.</summary>
    private void RefreshCurrentAlbums()
    {
        CurrentAlbums.Clear();
        if (Current is not { } item)
        {
            OnPropertyChanged(nameof(CurrentAlbumsText));
            return;
        }
        foreach (var album in Albums)
            CurrentAlbums.Add(new GalleryMembership(album, album.Keys.Contains(item.Key), SetMembership));
        OnPropertyChanged(nameof(CurrentAlbumsText));
    }

    /// <summary>Häkchen in der Einzelansicht: legt den Eintrag ins Album oder nimmt ihn heraus.</summary>
    private void SetMembership(GalleryAlbum album, bool member)
    {
        if (Current is not { } item) return;
        if (member)
        {
            Entry(item);
            if (!album.Keys.Add(item.Key)) return;
            Status = $"„{item.Name}“ liegt jetzt in „{album.Name}“.";
        }
        else
        {
            if (!album.Keys.Remove(item.Key)) return;
            Status = $"„{item.Name}“ aus „{album.Name}“ entfernt.";
        }
        album.RefreshCount();
        RefreshVisibleAlbums();
        OnPropertyChanged(nameof(CurrentAlbumsText));
        Persist();
        RaiseSelection();
        if (Section == GallerySection.Album && OpenAlbumEntry == album) Rebuild();
    }

    private void TagSelection(bool add)
    {
        var tags = GalleryTags.Parse(TagInput);
        if (tags.Count == 0) return;
        if (add) foreach (var tag in tags) _knownTags.Add(tag);
        foreach (var item in _selection)
        {
            var combined = add
                ? item.Tags.Concat(tags)
                : item.Tags.Where(tag => !tags.Contains(tag, StringComparer.Ordinal));
            var result = GalleryTags.Normalize(combined);
            item.SetTags(result);
            Entry(item).Tags = result;
        }
        Persist();
        RefreshScopedLists();
        RefreshCurrentTags();
        Status = add
            ? $"{tags.Count} Tag(s) auf {_selection.Count} Einträge angewendet."
            : $"{tags.Count} Tag(s) von {_selection.Count} Einträgen entfernt.";
        if (Section == GallerySection.Tag) Rebuild();
    }

    private GalleryEntryRecord Entry(GalleryItem item)
    {
        if (!_entries.TryGetValue(item.Key, out var entry))
            _entries[item.Key] = entry = new GalleryEntryRecord { Path = item.Key };
        entry.Name = item.Name;
        entry.Size = item.Size;
        return entry;
    }

    /// <summary>Schreibt Ordner, Ordnung und Alben zurück; leere Einträge fallen dabei heraus.</summary>
    private void Persist()
    {
        // Auch reine Albummitglieder bleiben stehen — sonst verlöre eine verschobene Datei ihr Album.
        var inAlbums = new HashSet<string>(Albums.SelectMany(album => album.Keys), StringComparer.OrdinalIgnoreCase);
        _document.Entries = [.. _entries.Values
            .Where(entry => entry.Favorite || entry.Tags.Count > 0 || inAlbums.Contains(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)];
        _document.Albums = GalleryStore.FromAlbums(Albums);
        _document.Tags = [.. _knownTags];
        if (_dirtyProbes)
        {
            _document.Probes = [.. _probes.Values];
            _dirtyProbes = false;
        }
        GalleryStore.Save(_document);
    }

    // ------------------------------------------------------------ Ansicht eines Eintrags

    private void OpenViewer(GalleryItem? item)
    {
        if (item is null) return;
        Current = item;
        IsViewerOpen = true;
    }

    private void Step(int direction)
    {
        if (Current is null || _filtered.Count == 0) return;
        var index = _filtered.IndexOf(Current);
        if (index < 0) return;
        var next = index + direction;
        if (next < 0 || next >= _filtered.Count) return;
        Current = _filtered[next];
    }

    private async Task LoadViewerAsync()
    {
        if (Current is not { } item) { ViewerImage = null; ViewerVideo = null; return; }
        if (item.IsVideo)
        {
            ViewerImage = null;
            ViewerVideo = File.Exists(item.FullPath) ? new Uri(item.FullPath) : null;
            return;
        }
        ViewerVideo = null;
        var path = item.FullPath;
        ViewerImage = await Task.Run(() => GalleryService.Load(path, 1600));
    }

    private void Reveal(GalleryItem? item)
    {
        if (item is null || !File.Exists(item.FullPath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Explorer ließ sich nicht öffnen: " + ex.Message; }
    }

    /// <summary>Ordner der Bibliotheksdatei im Explorer zeigen — dort liegen „library.json“ und die Vorschaubilder.</summary>
    private void OpenDataFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(GalleryStore.Directory);
            // Existiert die Datei schon, markiert der Explorer sie gleich mit.
            var arguments = File.Exists(GalleryStore.FilePath)
                ? $"/select,\"{GalleryStore.FilePath}\""
                : $"\"{GalleryStore.Directory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = "Ordner ließ sich nicht öffnen: " + ex.Message; }
    }

    private void OpenExternal(GalleryItem? item)
    {
        if (item is null || !File.Exists(item.FullPath)) return;
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Datei ließ sich nicht öffnen: " + ex.Message; }
    }
}
