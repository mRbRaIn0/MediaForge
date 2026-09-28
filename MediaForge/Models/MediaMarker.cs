using MediaForge.Core;

namespace MediaForge.Models;

public enum MarkerKind { Point, Range }

/// <summary>
/// Ein Marker in einer Mediendatei — entweder ein einzelner Zeitpunkt oder ein Bereich.
/// Zeiten liegen intern immer in Millisekunden vor, die Anzeige formatiert nach HH:MM:SS.
/// </summary>
public sealed class MediaMarker : ObservableObject
{
    private MarkerKind _kind;
    private long _startMs;
    private long _endMs;
    private string _title = string.Empty;
    private string _note = string.Empty;
    private string _category = string.Empty;
    private IReadOnlyList<string> _tags = [];
    private string? _id;

    /// <summary>Id nach Art des Markers („marker-…“ bzw. „range-…“); wird beim ersten Zugriff vergeben.</summary>
    public string Id { get => _id ??= NewId(Kind); set => _id = value; }

    public static string NewId(MarkerKind kind) =>
        (kind == MarkerKind.Range ? "range-" : "marker-") + Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MarkerKind Kind
    {
        get => _kind;
        set { if (SetProperty(ref _kind, value)) NotifyDisplay(); }
    }

    public long StartMs
    {
        get => _startMs;
        set { if (SetProperty(ref _startMs, Math.Max(0, value))) NotifyDisplay(); }
    }

    /// <summary>Nur für Bereich-Marker relevant; bei Punkt-Markern gleich <see cref="StartMs"/>.</summary>
    public long EndMs
    {
        get => _endMs;
        set { if (SetProperty(ref _endMs, Math.Max(0, value))) NotifyDisplay(); }
    }

    public string Title
    {
        get => _title;
        set { if (SetProperty(ref _title, value)) NotifyDisplay(); }
    }

    public string Note { get => _note; set => SetProperty(ref _note, value); }

    public string Category
    {
        get => _category;
        set { if (SetProperty(ref _category, value)) { OnPropertyChanged(nameof(CategoryColor)); OnPropertyChanged(nameof(CategoryText)); } }
    }

    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set { if (SetProperty(ref _tags, value)) OnPropertyChanged(nameof(TagsText)); }
    }

    public bool IsRange => Kind == MarkerKind.Range;
    public double StartSeconds => Formatters.ToSeconds(StartMs);
    public double EndSeconds => Formatters.ToSeconds(IsRange ? Math.Max(EndMs, StartMs) : StartMs);
    public long DurationMs => IsRange ? Math.Max(0, EndMs - StartMs) : 0;
    public string KindText => IsRange ? "Bereich" : "Punkt";
    public string CategoryText => string.IsNullOrWhiteSpace(Category) ? "—" : Category;
    public string CategoryColor => MarkerCategories.Color(Category);
    public string TagsText => Tags.Count == 0 ? string.Empty : string.Join(' ', Tags.Select(t => '#' + t));
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(ohne Titel)" : Title;

    public string TimeText => IsRange
        ? $"{Formatters.TimeFromMs(StartMs)} → {Formatters.TimeFromMs(Math.Max(EndMs, StartMs))}"
        : Formatters.TimeFromMs(StartMs);

    public string MetaText
    {
        get
        {
            var parts = new List<string> { KindText };
            if (IsRange) parts.Add(Formatters.TimeFromMs(DurationMs) + " lang");
            if (!string.IsNullOrWhiteSpace(Category)) parts.Add(Category);
            if (Tags.Count > 0) parts.Add(TagsText);
            return string.Join("  ·  ", parts);
        }
    }

    public MediaMarker Clone()
    {
        var copy = new MediaMarker { Id = Id, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt };
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Übernimmt alle Inhalte aus <paramref name="other"/>; die Id bleibt erhalten.</summary>
    public void CopyFrom(MediaMarker other)
    {
        Kind = other.Kind;
        StartMs = other.StartMs;
        EndMs = other.EndMs;
        Title = other.Title;
        Note = other.Note;
        Category = other.Category;
        Tags = [.. other.Tags];
    }

    /// <summary>Sucht den Marker anhand von Titel, Notiz, Kategorie und Tags.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var term in query.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var needle = term.TrimStart('#');
            if (needle.Length == 0) continue;
            var hit = Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                      Note.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                      Category.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                      TimeText.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                      Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (!hit) return false;
        }
        return true;
    }

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(IsRange));
        OnPropertyChanged(nameof(StartSeconds));
        OnPropertyChanged(nameof(EndSeconds));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(DisplayTitle));
    }
}

/// <summary>Vorschläge und Farben für Marker-Kategorien; freie Eingaben bleiben erlaubt.</summary>
public static class MarkerCategories
{
    public static readonly string[] Suggestions =
        ["Highlight", "Tor", "Interview", "Spielszene", "Fehler", "Kapitel", "Intro", "Werbung", "Notiz"];

    public const string Fallback = "#9EB9FF";

    public static string Color(string? category) => (category ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "highlight" => "#E0B84A",
        "tor" => "#4CAF7D",
        "interview" => "#45A9CE",
        "spielszene" => "#5B8CFF",
        "fehler" => "#E16363",
        "kapitel" => "#A78BFA",
        "intro" => "#3FB8A2",
        "werbung" => "#8FA0B8",
        _ => Fallback
    };
}
