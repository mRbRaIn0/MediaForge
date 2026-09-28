using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

/// <summary>Eingabemodell des Marker-Dialogs: prüft die Zeiten und schreibt sie in den Marker zurück.</summary>
public sealed class MarkerEditViewModel : ObservableObject
{
    private readonly MediaMarker _marker;
    private readonly long _durationMs;
    private bool _isRange;
    private string _startText;
    private string _endText;
    private string _title;
    private string _note;
    private string _tagsText;
    private string _category;
    private string _error = string.Empty;

    public MarkerEditViewModel(MediaMarker marker, bool isNew, long durationMs)
    {
        _marker = marker;
        _durationMs = durationMs;
        IsNew = isNew;
        _isRange = marker.IsRange;
        _startText = Formatters.TimeFromMs(marker.StartMs, withMilliseconds: true);
        _endText = Formatters.TimeFromMs(marker.IsRange ? Math.Max(marker.EndMs, marker.StartMs) : marker.StartMs, withMilliseconds: true);
        _title = marker.Title;
        _note = marker.Note;
        _tagsText = marker.TagsText;
        _category = marker.Category;
    }

    public bool IsNew { get; }
    public string[] Categories { get; } = MarkerCategories.Suggestions;
    public string HeaderText => IsNew ? "Marker setzen" : "Marker bearbeiten";
    public bool CanDelete => !IsNew;
    public string DurationHint => _durationMs > 0 ? $"Videolänge {Formatters.TimeFromMs(_durationMs)}" : string.Empty;

    public bool IsRange
    {
        get => _isRange;
        set { if (SetProperty(ref _isRange, value)) { OnPropertyChanged(nameof(IsPoint)); OnPropertyChanged(nameof(TypeHint)); } }
    }

    public bool IsPoint
    {
        get => !_isRange;
        set { if (value) IsRange = false; }
    }

    public string TypeHint => IsRange
        ? "Bereich — Start und Ende, z. B. ein Interview oder eine Spielszene."
        : "Punkt — ein einzelner Zeitpunkt, z. B. ein Tor oder eine Aussage.";

    public string StartText { get => _startText; set => SetProperty(ref _startText, value); }
    public string EndText { get => _endText; set => SetProperty(ref _endText, value); }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Note { get => _note; set => SetProperty(ref _note, value); }
    public string TagsText { get => _tagsText; set => SetProperty(ref _tagsText, value); }
    public string Category { get => _category; set => SetProperty(ref _category, value); }

    public string Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error.Length > 0;

    /// <summary>Prüft die Eingaben und überträgt sie in den Marker.</summary>
    public bool TryApply()
    {
        var start = Formatters.ParseTimeToMs(StartText);
        if (start is null) { Error = "Zeitpunkt bitte als HH:MM:SS oder HH:MM:SS.mmm angeben."; return false; }
        var end = start.Value;
        if (IsRange)
        {
            var parsed = Formatters.ParseTimeToMs(EndText);
            if (parsed is null) { Error = "Endzeit bitte als HH:MM:SS oder HH:MM:SS.mmm angeben."; return false; }
            if (parsed.Value <= start.Value) { Error = "Das Ende muss nach dem Start liegen."; return false; }
            end = parsed.Value;
        }
        if (_durationMs > 0 && start.Value > _durationMs)
        {
            Error = $"Der Zeitpunkt liegt hinter dem Videoende ({Formatters.TimeFromMs(_durationMs)}).";
            return false;
        }
        if (_durationMs > 0 && end > _durationMs) end = _durationMs;

        _marker.Kind = IsRange ? MarkerKind.Range : MarkerKind.Point;
        _marker.StartMs = start.Value;
        _marker.EndMs = end;
        _marker.Title = Title.Trim();
        _marker.Note = Note.Trim();
        _marker.Category = Category.Trim();
        _marker.Tags = SidecarService.NormalizeTags(TagsText);
        _marker.UpdatedAt = DateTimeOffset.UtcNow;
        Error = string.Empty;
        return true;
    }
}
