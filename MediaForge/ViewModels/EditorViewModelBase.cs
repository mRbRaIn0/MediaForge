using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public abstract class EditorViewModelBase : ObservableObject, IToolAware
{
    private string? _sourcePath;
    private MediaInfo? _info;
    private double _position;
    private bool _busy;
    private string _status = "Bereit";
    private double _progress;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _previewCts;
    private ImageSource? _previewImage;

    protected EditorViewModelBase(FfmpegService ffmpeg, Action back)
    {
        Ffmpeg = ffmpeg;
        BackCommand = new RelayCommand(_ => back());
    }

    protected FfmpegService Ffmpeg { get; }
    public RelayCommand BackCommand { get; }
    public ObservableCollection<ImageSource> Thumbnails { get; } = [];
    public ImageSource? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }
    public string? SourcePath { get => _sourcePath; protected set { if (SetProperty(ref _sourcePath, value)) { OnPropertyChanged(nameof(SourceName)); OnPropertyChanged(nameof(HasMedia)); } } }
    public string SourceName => SourcePath is null ? "Keine Datei geladen" : Formatters.MiddleEllipsis(Path.GetFileName(SourcePath), 60);
    public bool HasMedia => SourcePath is not null;
    public MediaInfo? Info { get => _info; protected set { if (SetProperty(ref _info, value)) { OnPropertyChanged(nameof(Duration)); OnPropertyChanged(nameof(MediaSummary)); } } }
    public double Duration => Info?.Duration ?? 0;
    public double Position { get => _position; set => SetProperty(ref _position, Math.Clamp(value, 0, Math.Max(0, Duration))); }
    public bool IsBusy { get => _busy; protected set => SetProperty(ref _busy, value); }
    public string Status { get => _status; protected set => SetProperty(ref _status, value); }
    public double Progress { get => _progress; protected set => SetProperty(ref _progress, Math.Clamp(value, 0, 1)); }
    public bool HasFfmpeg => Ffmpeg.IsAvailable;
    public string MediaSummary => Info is null ? "—" : $"{Formatters.Time(Info.Duration)}  |  {(Info.HasVideo ? $"{Info.Width}x{Info.Height}" : "nur Audio")}  |  V:{Info.VideoCodec} A:{Info.AudioCodec}";

    public void RefreshTools() { Ffmpeg.Refresh(); OnPropertyChanged(nameof(HasFfmpeg)); }

    protected async Task LoadMediaAsync(string path)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        SourcePath = path;
        Position = 0;
        Status = "Prüfe Datei…";
        Info = await Ffmpeg.ProbeAsync(path, _thumbnailCts.Token);
        Status = Info.Ok ? "Bereit" : "Datei konnte nicht geprüft werden.";
        await OnMediaLoadedAsync();
        await UpdatePreviewAsync(0);
        _ = GenerateThumbnailsAsync(_thumbnailCts.Token);
    }

    protected virtual Task OnMediaLoadedAsync() => Task.CompletedTask;

    public async Task UpdatePreviewAsync(double time)
    {
        if (SourcePath is null || Info?.HasVideo != true) return;
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var path = SourcePath;
        try
        {
            var bytes = await Ffmpeg.ExtractFrameAsync(path, time, 480, _previewCts.Token);
            if (bytes is null || path != SourcePath) return;
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            PreviewImage = image;
        }
        catch (OperationCanceledException) { }
    }

    private async Task GenerateThumbnailsAsync(CancellationToken token)
    {
        Thumbnails.Clear();
        if (Info is not { HasVideo: true, Duration: > 0 } || SourcePath is null) return;
        for (var i = 0; i < 14; i++)
        {
            token.ThrowIfCancellationRequested();
            var time = (i + .5) * Duration / 14;
            var bytes = await Ffmpeg.ExtractFrameAsync(SourcePath, time, 160, token);
            if (bytes is null) continue;
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Thumbnails.Add(image);
        }
    }
}
