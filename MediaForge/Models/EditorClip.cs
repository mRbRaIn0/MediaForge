using MediaForge.Core;

namespace MediaForge.Models;

/// <summary>Ein Abschnitt der Quelldatei in der Schnittliste; der Export hängt die Clips in Listenreihenfolge aneinander.</summary>
public sealed class EditorClip : ObservableObject
{
    private long _startMs;
    private long _endMs;
    private int _number;
    private bool _selected;

    public EditorClip(long startMs, long endMs, string? label = null)
    {
        _startMs = startMs;
        _endMs = Math.Max(startMs, endMs);
        Label = label ?? string.Empty;
    }

    public string Label { get; }

    public int Number
    {
        get => _number;
        set { if (SetProperty(ref _number, value)) OnPropertyChanged(nameof(Display)); }
    }

    public long StartMs
    {
        get => _startMs;
        set { if (SetProperty(ref _startMs, Math.Max(0, value))) Notify(); }
    }

    public long EndMs
    {
        get => _endMs;
        set { if (SetProperty(ref _endMs, Math.Max(0, value))) Notify(); }
    }

    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

    public double StartSeconds => Formatters.ToSeconds(StartMs);
    public double EndSeconds => Formatters.ToSeconds(EndMs);
    public long DurationMs => Math.Max(0, EndMs - StartMs);
    public string TimeText => $"{Formatters.TimeFromMs(StartMs)} → {Formatters.TimeFromMs(EndMs)}";
    public string DurationText => Formatters.TimeFromMs(DurationMs);
    public bool HasLabel => Label.Length > 0;
    public string Display => string.IsNullOrEmpty(Label) ? TimeText : $"{TimeText}   {Label}";

    public bool Contains(long ms) => ms > StartMs && ms < EndMs;

    private void Notify()
    {
        OnPropertyChanged(nameof(StartSeconds));
        OnPropertyChanged(nameof(EndSeconds));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Display));
    }
}
