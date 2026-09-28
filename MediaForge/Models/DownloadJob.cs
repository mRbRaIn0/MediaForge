using MediaForge.Core;

namespace MediaForge.Models;

public enum JobState { Pending, Running, Upscaling, Done, Error, Skipped }

public sealed class DownloadJob : ObservableObject
{
    private string _title;
    private string _detail;
    private string? _thumbnailUrl;
    private double _progress;
    private JobState _state;
    private bool _canSkip;

    public DownloadJob(int number, string url, string mode, string section, string quality, string format,
        string ratio, string encoder, string upscaler, bool keepOriginal, bool embedThumbnail = false,
        bool preferH264 = true, bool convertToH264 = false)
    {
        Number = number;
        Url = url;
        Mode = mode;
        Section = section;
        Quality = quality;
        Format = format;
        Ratio = ratio;
        Encoder = encoder;
        Upscaler = upscaler;
        KeepOriginal = keepOriginal;
        EmbedThumbnail = embedThumbnail;
        PreferH264 = preferH264;
        ConvertToH264 = convertToH264;
        _title = url;
        _detail = BuildDefaultDetail();
    }

    public int Number { get; }
    public string Url { get; }
    public string Mode { get; }
    public string Section { get; }
    public string Quality { get; }
    public string Format { get; }
    public string Ratio { get; }
    public string Encoder { get; }
    public string Upscaler { get; }
    public bool KeepOriginal { get; }
    public bool EmbedThumbnail { get; }
    public bool PreferH264 { get; }
    public bool ConvertToH264 { get; }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public string? ThumbnailUrl { get => _thumbnailUrl; set => SetProperty(ref _thumbnailUrl, value); }
    public double Progress { get => _progress; set => SetProperty(ref _progress, Math.Clamp(value, 0, 1)); }
    public JobState State { get => _state; set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); } } }
    public bool CanSkip { get => _canSkip; set => SetProperty(ref _canSkip, value); }
    public string StatusText => State switch
    {
        JobState.Running => "Lädt…", JobState.Upscaling => "AI Upscale", JobState.Done => "Fertig",
        JobState.Error => "Fehler", JobState.Skipped => "Überspr.", _ => "Wartend"
    };
    public string StatusColor => State switch
    {
        JobState.Running => "#E0B84A", JobState.Upscaling => "#5B8CFF", JobState.Done => "#4CAF7D",
        JobState.Error => "#E16363", JobState.Skipped => "#D9A441", _ => "#939CAA"
    };

    public DownloadJob CloneForRetry(int number) => new(number, Url, Mode, Section, Quality, Format, Ratio, Encoder,
        Upscaler, KeepOriginal, EmbedThumbnail, PreferH264, ConvertToH264);

    private string BuildDefaultDetail()
    {
        var result = $"{Mode.ToUpperInvariant()} | {Quality} | {Format}";
        if (Ratio != "Original") result += $" | {Ratio} | {Encoder}";
        if (Upscaler != "Aus") result += $" | AI: {Upscaler}" + (KeepOriginal ? string.Empty : " | Original löschen");
        if (EmbedThumbnail) result += " | Thumbnail einbetten";
        if (!PreferH264 && Mode == "video" && Format == "MP4") result += " | beste Videoqualität";
        if (!PreferH264 && ConvertToH264 && Mode == "video" && Format == "MP4") result += " | danach H.264";
        if (!string.IsNullOrWhiteSpace(Section)) result += $" | Cut: {Section}";
        return result;
    }
}
