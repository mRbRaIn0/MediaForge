using MediaForge.Core;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly FfmpegService _ffmpeg;
    private readonly YtDlpService _ytDlp;
    private readonly Action _refreshAll;
    private string _ffmpegStatus = string.Empty;
    private string _ytDlpStatus = string.Empty;
    private string _outputStatus = string.Empty;

    public HomeViewModel(FfmpegService ffmpeg, YtDlpService ytDlp, Action<string> navigate, Action refreshAll)
    {
        _ffmpeg = ffmpeg;
        _ytDlp = ytDlp;
        _refreshAll = refreshAll;
        NavigateCommand = new RelayCommand(p => navigate(p?.ToString() ?? "home"));
        Refresh();
    }

    public RelayCommand NavigateCommand { get; }
    public string FfmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string YtDlpStatus { get => _ytDlpStatus; private set => SetProperty(ref _ytDlpStatus, value); }
    public string OutputStatus { get => _outputStatus; private set => SetProperty(ref _outputStatus, value); }
    public bool HasFfmpeg => _ffmpeg.IsAvailable;

    public void Refresh()
    {
        _ffmpeg.Refresh();
        _ytDlp.Refresh();
        FfmpegStatus = _ffmpeg.FfmpegPath is null
            ? "FFmpeg nicht gefunden — im yt-dlp Bereich über „FFmpeg Update“ laden."
            : $"FFmpeg: {_ffmpeg.FfmpegPath}";
        YtDlpStatus = _ytDlp.ExecutablePath is null
            ? "yt-dlp nicht gefunden — im yt-dlp Bereich über „yt-dlp Update“ laden."
            : $"yt-dlp: {_ytDlp.ExecutablePath}";
        OutputStatus = $"Zielordner: {SettingsService.OutputDirectory(useCustom: false)}";
        OnPropertyChanged(nameof(HasFfmpeg));
    }

    /// <summary>Wird nach dem Speichern der Einstellungen aufgerufen und aktualisiert alle Seiten.</summary>
    public void ApplySettings() => _refreshAll();
}
