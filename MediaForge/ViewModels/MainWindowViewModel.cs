using MediaForge.Core;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly Dictionary<string, object> _pages;
    private object _currentPage;

    public MainWindowViewModel()
    {
        var ffmpeg = new FfmpegService();
        var ytDlp = new YtDlpService();
        _pages = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        _pages["download"] = new DownloadViewModel(ffmpeg, ytDlp, ShowHome);
        _pages["cut"] = new CutViewModel(ffmpeg, ShowHome);
        _pages["editor"] = new EditorViewModel(ffmpeg, ShowHome);
        _pages["convert"] = new ConvertViewModel(ffmpeg, ShowHome);
        _pages["speed"] = new SpeedViewModel(ffmpeg, ShowHome);
        _pages["link"] = new LinkViewModel(ffmpeg, ShowHome);
        _pages["sorter"] = new SorterViewModel(ShowHome);
        _pages["gallery"] = new GalleryViewModel(ffmpeg, ShowHome);
        _pages["home"] = new HomeViewModel(ffmpeg, ytDlp, Navigate, RefreshToolState);
        _currentPage = _pages["home"];
    }

    public object CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value)) OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public string WindowTitle => CurrentPage switch
    {
        DownloadViewModel => "yt-dlp PowerUI v8 — MediaForge",
        CutViewModel => "Video schneiden — MediaForge",
        EditorViewModel => "Editor — MediaForge",
        ConvertViewModel => "Video konvertieren — MediaForge",
        SpeedViewModel => "Speed Zone — MediaForge",
        LinkViewModel => "Link — MediaForge",
        SorterViewModel => "Sorter — MediaForge",
        GalleryViewModel => "Galerie — MediaForge",
        _ => "MediaForge"
    };

    private void Navigate(string key)
    {
        if (_pages.TryGetValue(key, out var page)) CurrentPage = page;
    }

    private void ShowHome()
    {
        RefreshToolState();
        CurrentPage = _pages["home"];
    }

    private void RefreshToolState()
    {
        if (_pages.TryGetValue("home", out var home) && home is HomeViewModel vm) vm.Refresh();
        foreach (var page in _pages.Values)
            if (page is IToolAware aware) aware.RefreshTools();
    }
}

public interface IToolAware { void RefreshTools(); }
