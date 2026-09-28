using MediaForge.Core;

namespace MediaForge.Models;

public sealed class ConvertJobModel : ObservableObject
{
    private MediaInfo? _info;
    private JobState _state;
    public required int Number { get; init; }
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public MediaInfo? Info { get => _info; set { if (SetProperty(ref _info, value)) OnPropertyChanged(nameof(CodecText)); } }
    public JobState State { get => _state; set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); } } }
    public string CodecText => Info is null ? "prüfe…" : $"{Info.VideoCodec} / {Info.AudioCodec}";
    public string StatusText => State switch { JobState.Running => "Läuft…", JobState.Done => "Fertig", JobState.Error => "Fehler", JobState.Skipped => "OK (überspr.)", _ => "Wartend" };
    public string StatusColor => State switch { JobState.Running => "#E0B84A", JobState.Done => "#4CAF7D", JobState.Error => "#E16363", _ => "#939CAA" };
}

public sealed class LinkItemModel : ObservableObject
{
    private MediaInfo? _info;
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public MediaInfo? Info { get => _info; set { if (SetProperty(ref _info, value)) OnPropertyChanged(nameof(Meta)); } }
    public string Meta => Info is null ? "prüfe…" : Info.HasVideo ? $"{Info.Width}x{Info.Height} · {Info.VideoCodec}" : "kein Video";
}

public sealed class SpeedZone : ObservableObject
{
    public required double Start { get; init; }
    public required double End { get; init; }
    public required double Factor { get; init; }
    public string Display => $"{Formatters.Time(Start)[..8]} → {Formatters.Time(End)[..8]}   {Factor:g}× ({(Factor > 1 ? "schneller" : "langsamer")})";
}

public sealed record SpeedSegment(double Start, double End, double Factor);
