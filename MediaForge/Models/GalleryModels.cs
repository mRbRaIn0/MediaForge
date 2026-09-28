using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using MediaForge.Core;

namespace MediaForge.Models;

/// <summary>Was die Galerie anzeigt — Bilder und Videos, sonst nichts.</summary>
public enum GalleryKind { Image, Video }

/// <summary>Welcher Bereich gerade offen ist; bestimmt, aus welchem Vorrat gefiltert wird.</summary>
public enum GallerySection { All, Favorites, Notes, Album, Tag, Folder }

/// <summary>
/// Ein Eintrag der Galerie: die Datei selbst plus die Ordnung, die die Galerie darüber legt.
/// Die Datei wird nie verändert — Favorit, Tags und Albumzugehörigkeit leben in der Bibliotheksdatei.
/// </summary>
public sealed class GalleryItem : ObservableObject
{
    private bool _isFavorite;
    private bool _isSelected;
    private ImageSource? _thumbnail;
    private long _durationMs;
    private List<string> _tags = [];

    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string RootPath { get; init; }

    /// <summary>Unterordner relativ zum registrierten Ordner; leer bedeutet direkt in der Wurzel.</summary>
    public string Subfolder { get; init; } = string.Empty;

    public GalleryKind Kind { get; init; }

    /// <summary>Dateiart in Großbuchstaben ohne Punkt, z. B. „MP4“ — Sortier- und Gruppierschlüssel.</summary>
    public string Extension { get; init; } = string.Empty;

    public long Size { get; init; }
    public long ModifiedTicks { get; init; }

    /// <summary>Aufnahmedatum aus den Bilddaten, sonst das Änderungsdatum der Datei.</summary>
    public DateTime TakenAt { get; set; }

    /// <summary>Kleinschreibweise des Pfads — der Schlüssel, unter dem die Bibliothek den Eintrag kennt.</summary>
    public string Key => FullPath.ToLowerInvariant();

    public bool IsVideo => Kind == GalleryKind.Video;

    public long DurationMs
    {
        get => _durationMs;
        set { if (SetProperty(ref _durationMs, value)) OnPropertyChanged(nameof(DurationText)); }
    }

    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }

    /// <summary>Wurde für diesen Eintrag schon ein Vorschaubild angefordert?</summary>
    public bool ThumbnailRequested { get; set; }

    public IReadOnlyList<string> Tags => _tags;

    public void SetTags(IEnumerable<string> tags)
    {
        _tags = GalleryTags.Normalize(tags);
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(HasTags));
    }

    public string TagsText => _tags.Count == 0 ? string.Empty : string.Join("  ", _tags.Select(tag => "#" + tag));
    public bool HasTags => _tags.Count > 0;

    public string SizeText => Formatters.Size(Size);
    public string DurationText => IsVideo && DurationMs > 0 ? ShortTime(DurationMs) : string.Empty;
    public string FolderText => Subfolder.Length == 0 ? "Hauptordner" : Subfolder;
    public string TakenText => TakenAt.ToString("dddd, d. MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Der Tag, unter dem der Eintrag in der Zeitleiste einsortiert wird.</summary>
    public DateTime Day => TakenAt.Date;

    private static string ShortTime(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}

/// <summary>
/// Eine Gruppe der Zeitleiste — je nach Sortierung ein Tag, ein Anfangsbuchstabe, eine Dateiart
/// oder die eine Gruppe, die alles aufnimmt.
/// </summary>
public sealed class GalleryDay
{
    /// <summary>Womit entschieden wird, ob ein Eintrag noch in diese Gruppe gehört.</summary>
    public required string Key { get; init; }

    public required string Header { get; init; }
    public ObservableCollection<GalleryItem> Items { get; } = [];
    public string CountText => Items.Count == 1 ? "1 Eintrag" : $"{Items.Count} Einträge";
}

/// <summary>Ein Album ist eine gespeicherte Auswahl, kein Ordner — eine Datei darf in mehreren liegen.</summary>
public sealed class GalleryAlbum : ObservableObject
{
    private string _name = string.Empty;
    private int _count;
    private bool _isChecked;
    private string _selectionHint = string.Empty;
    private bool _isRenaming;
    private string _editName = string.Empty;
    private int _scopedCount;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Schlüssel der enthaltenen Einträge (Pfad in Kleinschreibung).</summary>
    public HashSet<string> Keys { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public int Count { get => _count; set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(CountText)); } }

    /// <summary>Wie viele davon im gerade gewählten Ordner liegen; ohne Ordnerauswahl alle.</summary>
    public int ScopedCount { get => _scopedCount; set => SetProperty(ref _scopedCount, value); }
    public string CountText => Count == 1 ? "1 Eintrag" : $"{Count} Einträge";

    /// <summary>Ist dieses Album Ziel der nächsten Sammelaktion? Standardmäßig keines.</summary>
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    /// <summary>„3/5“ — wie viele der ausgewählten Einträge schon drin liegen.</summary>
    public string SelectionHint
    {
        get => _selectionHint;
        set { if (SetProperty(ref _selectionHint, value)) OnPropertyChanged(nameof(HasSelectionHint)); }
    }

    public bool HasSelectionHint => SelectionHint.Length > 0;

    /// <summary>Wird der Name gerade in der Seitenleiste bearbeitet?</summary>
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }

    /// <summary>Der Zwischenstand der Eingabe; erst beim Bestätigen wird daraus der Name.</summary>
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }

    public void RefreshCount() => Count = Keys.Count;
}

/// <summary>
/// Ein Album aus Sicht eines einzelnen Eintrags: das Häkchen zeigt und ändert zugleich,
/// ob der Eintrag darin liegt.
/// </summary>
public sealed class GalleryMembership(GalleryAlbum album, bool isMember, Action<GalleryAlbum, bool> apply)
    : ObservableObject
{
    private bool _isMember = isMember;

    public GalleryAlbum Album { get; } = album;
    public string Name => Album.Name;

    public bool IsMember
    {
        get => _isMember;
        set { if (SetProperty(ref _isMember, value)) apply(Album, value); }
    }
}

/// <summary>
/// Ein Tag aus Sicht eines einzelnen Eintrags: das Häkchen zeigt und ändert zugleich,
/// ob der Eintrag ihn trägt.
/// </summary>
public sealed class GalleryTagMembership(string name, bool isMember, Action<string, bool> apply) : ObservableObject
{
    private bool _isMember = isMember;

    public string Name { get; } = name;
    public string Label => "#" + Name;

    public bool IsMember
    {
        get => _isMember;
        set { if (SetProperty(ref _isMember, value)) apply(Name, value); }
    }
}

/// <summary>Ein Tag mit der Anzahl der Einträge, die ihn tragen — für die Seitenleiste.</summary>
public sealed class GalleryTag(string name, int count) : ObservableObject
{
    private bool _isRenaming;
    private string _editName = name;

    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Label => "#" + Name;

    /// <summary>Wird der Name gerade in der Seitenleiste bearbeitet?</summary>
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }

    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
}

/// <summary>Ein registrierter Ordner der Bibliothek samt der Unterordner, die er mitgebracht hat.</summary>
public sealed class GalleryRoot(string path, int count) : ObservableObject
{
    public string Path { get; } = path;
    public int Count { get; } = count;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : Path;
    public bool Exists => Directory.Exists(Path);

    /// <summary>Unterordner der ersten Ebene, in denen Bilder oder Videos liegen.</summary>
    public ObservableCollection<GalleryFolder> Subfolders { get; } = [];

    public bool HasSubfolders => Subfolders.Count > 0;

    /// <summary>Wurzelordner starten aufgeklappt; tiefere Ebenen holt man sich bei Bedarf.</summary>
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    private bool _isExpanded = true;
}

/// <summary>
/// Ein anklickbarer Ordner der Seitenleiste. Ein leerer <see cref="Relative"/> meint den Wurzelordner
/// selbst, sonst einen Unterordner darunter — mit allem, was noch tiefer liegt.
/// </summary>
public sealed class GalleryFolder(string rootPath, string relative, int count) : ObservableObject
{
    private bool _isExpanded;

    public string RootPath { get; } = rootPath;
    public string Relative { get; } = relative;
    public int Count { get; } = count;

    /// <summary>Die Ordner, die noch eine Ebene tiefer liegen.</summary>
    public ObservableCollection<GalleryFolder> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    /// <summary>Voller relativer Pfad — die Überschrift der geöffneten Sicht.</summary>
    public string Name => Relative.Length == 0
        ? System.IO.Path.GetFileName(RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar))
        : Relative;

    /// <summary>Nur der letzte Abschnitt — so steht der Ordner in der Seitenleiste.</summary>
    public string Label => Relative.Length == 0
        ? Name
        : Relative[(Relative.LastIndexOfAny([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]) + 1)..];

    public string FullPath => Relative.Length == 0 ? RootPath : System.IO.Path.Combine(RootPath, Relative);

    /// <summary>Gehört der Eintrag in diesen Ordner? Unterordner zählen mit.</summary>
    public bool Contains(GalleryItem item) =>
        string.Equals(item.RootPath, RootPath, StringComparison.OrdinalIgnoreCase) &&
        (Relative.Length == 0 ||
         string.Equals(item.Subfolder, Relative, StringComparison.OrdinalIgnoreCase) ||
         item.Subfolder.StartsWith(Relative + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Schreibweise und Zerlegung von Tags — überall gleich, damit „#VfB“ und „vfb“ dasselbe sind.</summary>
public static class GalleryTags
{
    /// <summary>Zerlegt eine Eingabezeile in einzelne Tags; „#“, Komma und Leerzeichen trennen.</summary>
    public static List<string> Parse(string? input) =>
        string.IsNullOrWhiteSpace(input)
            ? []
            : Normalize(input.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Vereinheitlicht: klein, ohne führendes „#“, ohne Dubletten, alphabetisch.</summary>
    public static List<string> Normalize(IEnumerable<string>? tags)
    {
        if (tags is null) return [];
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in tags)
        {
            var tag = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
            if (tag.Length > 0) set.Add(tag);
        }
        return [.. set];
    }

    public static string Format(IEnumerable<string>? tags) => string.Join(" ", Normalize(tags));
}
