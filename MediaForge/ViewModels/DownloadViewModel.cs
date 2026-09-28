using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed partial class DownloadViewModel : ObservableObject, IToolAware
{
    private readonly FfmpegService _ffmpeg;
    private readonly YtDlpService _ytDlp;
    private readonly Dictionary<string, Dictionary<string, string?>> _metadata = new(StringComparer.Ordinal);
    private readonly List<DownloadJob> _failedJobs = [];
    private string _url = string.Empty;
    private string _mode = "video";
    private string _quality = "Best";
    private string _format = "MP4";
    private string _ratio = "Original";
    private string _encoder = "CPU (Standard)";
    private string _upscaler = "Aus";
    private bool _keepOriginal = true;
    private bool _embedThumbnail;
    private bool _preferH264 = true;
    private bool _convertToH264;
    private bool _useMega = true;
    private string _cookieSelection = string.Empty;
    private string _rawCommand = string.Empty;
    private bool _isRunning;
    private DownloadJob? _currentJob;
    private CancellationTokenSource? _currentProcessCts;
    private bool _skipRequested;
    private string _appliedCookiePreference = string.Empty;
    private string? _appliedOutputDirectory;
    private readonly Dispatcher? _uiDispatcher;
    private readonly ConcurrentQueue<LogEntry> _pendingLogLines = new();
    private readonly Queue<string> _logHistory = new();
    private int _logFlushScheduled;
    private int _logGeneration;
    private const int MaxLogLines = 1200;

    public DownloadViewModel(FfmpegService ffmpeg, YtDlpService ytDlp, Action back)
    {
        _uiDispatcher = Application.Current?.Dispatcher;
        _ffmpeg = ffmpeg;
        _ytDlp = ytDlp;
        TimeSegments.Add(new TimeSegment());
        Jobs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(JobCount));
            OnPropertyChanged(nameof(JobCountText));
            OnPropertyChanged(nameof(HasJobs));
        };
        BackCommand = new RelayCommand(_ => back());
        AddTimeSegmentCommand = new RelayCommand(_ => AddTimeSegment(), _ => !IsRunning);
        RemoveTimeSegmentCommand = new RelayCommand(p => RemoveTimeSegment(p as TimeSegment), _ => !IsRunning && TimeSegments.Count > 1);
        // Neue Links dürfen während einer laufenden Queue angenommen werden. Sie werden
        // bewusst erst beim nächsten Queue-Start verarbeitet, damit der aktive Durchlauf
        // und dessen Reihenfolge unverändert bleiben.
        AddJobCommand = new RelayCommand(_ => AddManualJob());
        RemoveJobCommand = new RelayCommand(p => RemoveJob(p as DownloadJob), _ => !IsRunning);
        SkipJobCommand = new RelayCommand(p => SkipJob(p as DownloadJob), p => p is DownloadJob job && job == _currentJob && job.CanSkip);
        ImportCommand = new RelayCommand(_ => ImportUrls());
        StartCommand = new AsyncRelayCommand(_ => StartQueueAsync(), _ => !IsRunning);
        RawCommandCommand = new AsyncRelayCommand(_ => RunRawAsync(), _ => !IsRunning);
        UpdateCommand = new AsyncRelayCommand(_ => UpdateYtDlpAsync(), _ => !IsRunning);
        FfmpegUpdateCommand = new AsyncRelayCommand(_ => UpdateFfmpegAsync(), _ => !IsRunning);
        RetryFailedCommand = new RelayCommand(_ => RetryFailed(), _ => FailedCount > 0 && !IsRunning);
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        ApplySettings();
        Log($"--- System Ready | Speicherort: {GetOutputDirectory()} ---", "SYS");
    }

    public ObservableCollection<TimeSegment> TimeSegments { get; } = [];
    public ObservableCollection<DownloadJob> Jobs { get; } = [];
    public string[] VideoQualities { get; } = ["Best", "4K", "1080p", "720p", "480p"];
    public string[] AudioQualities { get; } = ["Best", "High", "Mid", "Low"];
    public string[] VideoFormats { get; } = ["MP4", "MKV", "WEBM"];
    public string[] AudioFormats { get; } = ["MP3", "M4A", "WAV", "FLAC"];
    public string[] Ratios { get; } = ["Original", "9:16 (Vertical)"];
    public string[] Encoders { get; } = ["CPU (Standard)", "GPU (NVIDIA)"];
    public string[] Upscalers { get; } = ["Aus", "Real-ESRGAN 2x", "Real-ESRGAN 4x"];
    public ObservableCollection<string> CookieOptions { get; } = [];
    public IEnumerable<string> AvailableQualities => Mode == "video" ? VideoQualities : AudioQualities;
    public IEnumerable<string> AvailableFormats => Mode == "video" ? VideoFormats : AudioFormats;
    public RelayCommand BackCommand { get; }
    public RelayCommand AddTimeSegmentCommand { get; }
    public RelayCommand RemoveTimeSegmentCommand { get; }
    public RelayCommand AddJobCommand { get; }
    public RelayCommand RemoveJobCommand { get; }
    public RelayCommand SkipJobCommand { get; }
    public RelayCommand ImportCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand RawCommandCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand FfmpegUpdateCommand { get; }
    public RelayCommand RetryFailedCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public string Url { get => _url; set { if (SetProperty(ref _url, value)) AddJobCommand.RaiseCanExecuteChanged(); } }
    public string Mode { get => _mode; set { if (SetProperty(ref _mode, value)) UpdateModeOptions(); } }
    public string Quality { get => _quality; set => SetProperty(ref _quality, value); }
    public string Format { get => _format; set { if (SetProperty(ref _format, value)) OnPropertyChanged(nameof(CanConvertToH264)); } }
    public string Ratio { get => _ratio; set => SetProperty(ref _ratio, value); }
    public string Encoder { get => _encoder; set => SetProperty(ref _encoder, value); }
    public string Upscaler { get => _upscaler; set => SetProperty(ref _upscaler, value); }
    public bool KeepOriginal { get => _keepOriginal; set => SetProperty(ref _keepOriginal, value); }
    public bool EmbedThumbnail { get => _embedThumbnail; set => SetProperty(ref _embedThumbnail, value); }
    public bool PreferH264 { get => _preferH264; set { if (SetProperty(ref _preferH264, value)) OnPropertyChanged(nameof(CanConvertToH264)); } }
    public bool ConvertToH264 { get => _convertToH264; set => SetProperty(ref _convertToH264, value); }
    public bool UseMega { get => _useMega; set { if (SetProperty(ref _useMega, value)) { OnPropertyChanged(nameof(OutputDirectory)); _appliedOutputDirectory = GetOutputDirectory(); Log("Speicherort: " + _appliedOutputDirectory, "SYS"); } } }
    public string CookieSelection { get => _cookieSelection; set => SetProperty(ref _cookieSelection, value); }
    public string RawCommand { get => _rawCommand; set { if (SetProperty(ref _rawCommand, value)) RawCommandCommand.RaiseCanExecuteChanged(); } }
    public string LogText { get { lock (_logHistory) return string.Concat(_logHistory); } }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) RaiseCommands(); } }
    public bool IsVideoMode => Mode == "video";
    public bool CanConvertToH264 => IsVideoMode && Format == "MP4" && !PreferH264;
    public event EventHandler<IReadOnlyList<string>>? LogLinesAppended;
    public event EventHandler? LogCleared;
    public int FailedCount => _failedJobs.Count;
    public string ErrorButtonText => $"Fehler ({FailedCount})";
    public int JobCount => Jobs.Count;
    public string JobCountText => $"{JobCount} {(JobCount == 1 ? "Eintrag" : "Einträge")}";
    public bool HasJobs => JobCount > 0;
    public string OutputDirectory => GetOutputDirectory();
    public bool HasYtDlp => _ytDlp.IsAvailable;
    public bool HasFfmpeg => _ffmpeg.IsAvailable;

    public void RefreshTools()
    {
        _ytDlp.Refresh(); _ffmpeg.Refresh();
        ApplySettings();
        OnPropertyChanged(nameof(HasYtDlp));
        OnPropertyChanged(nameof(HasFfmpeg));
    }

    /// <summary>
    /// Übernimmt Zielordner und Cookie-Vorgaben aus den Einstellungen. Eine im Bereich getroffene
    /// Cookie-Auswahl bleibt erhalten, solange sich die Vorgabe in den Einstellungen nicht ändert.
    /// </summary>
    private void ApplySettings()
    {
        var settings = SettingsService.Current;
        var previous = CookieSelection;
        var fileName = Path.GetFileName(SettingsService.ResolveDataFile(settings.CookieFile, "cookies.txt"));
        var browsers = new List<string> { "Chrome", "Firefox", "Edge", "Brave" };
        var configured = Capitalize(settings.CookieBrowser);
        if (configured.Length > 0 && !browsers.Contains(configured, StringComparer.OrdinalIgnoreCase)) browsers.Insert(0, configured);

        CookieOptions.Clear();
        CookieOptions.Add("Keine");
        foreach (var browser in browsers) CookieOptions.Add("Browser: " + browser);
        CookieOptions.Add("Datei: " + fileName);

        var preferred = settings.CookieMode switch
        {
            AppSettings.CookieModeBrowser => "Browser: " + configured,
            AppSettings.CookieModeFile => "Datei: " + fileName,
            _ => "Keine"
        };
        var preferenceChanged = !string.Equals(preferred, _appliedCookiePreference, StringComparison.Ordinal);
        CookieSelection = preferenceChanged && CookieOptions.Contains(preferred) ? preferred
            : CookieOptions.Contains(previous) ? previous
            : CookieOptions.Contains(preferred) ? preferred : CookieOptions[0];
        _appliedCookiePreference = preferred;

        OnPropertyChanged(nameof(OutputDirectory));
        var directory = GetOutputDirectory();
        if (_appliedOutputDirectory is not null && !string.Equals(directory, _appliedOutputDirectory, StringComparison.OrdinalIgnoreCase))
            Log("Speicherort: " + directory, "SYS");
        _appliedOutputDirectory = directory;
    }

    public string FailedJobsText => string.Join(Environment.NewLine, _failedJobs.Select(j => $"{j.Url} | {j.Quality} | {j.Format} | {j.Ratio} | {j.Encoder} | {j.Upscaler}"));

    private void UpdateModeOptions()
    {
        OnPropertyChanged(nameof(IsVideoMode));
        OnPropertyChanged(nameof(CanConvertToH264));
        OnPropertyChanged(nameof(AvailableQualities));
        OnPropertyChanged(nameof(AvailableFormats));
        Quality = "Best";
        Format = Mode == "video" ? "MP4" : "MP3";
        if (Mode != "video") Upscaler = "Aus";
    }

    private void RemoveTimeSegment(TimeSegment? segment)
    {
        if (segment is not null && TimeSegments.Count > 1) TimeSegments.Remove(segment);
        RemoveTimeSegmentCommand.RaiseCanExecuteChanged();
    }

    private void AddTimeSegment()
    {
        TimeSegments.Add(new TimeSegment());
        RemoveTimeSegmentCommand.RaiseCanExecuteChanged();
    }

    private void AddManualJob()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Log("Bitte zuerst eine URL eingeben.", "WARN");
            return;
        }
        var section = string.Join(',', TimeSegments.Select(s => s.ToSection()).Where(s => s is not null));
        var job = CreateJob(Url.Trim(), Mode, section, Quality, Format, Ratio, Encoder, Upscaler, KeepOriginal,
            EmbedThumbnail, PreferH264, ConvertToH264);
        Log(IsRunning
            ? "Für den nächsten Queue-Start hinzugefügt: " + job.Url
            : "Zur Warteschlange hinzugefügt: " + job.Url,
            "QUEUE");
        Url = string.Empty;
        TimeSegments.Clear(); TimeSegments.Add(new TimeSegment());
    }

    private DownloadJob CreateJob(string url, string mode, string section, string quality, string format,
        string ratio, string encoder, string upscaler = "Aus", bool keepOriginal = true, bool embedThumbnail = false,
        bool preferH264 = true, bool convertToH264 = false)
    {
        var job = new DownloadJob(Jobs.Count + 1, url, mode, section, quality, format, ratio, encoder, upscaler,
            keepOriginal, embedThumbnail, preferH264, convertToH264);
        Jobs.Add(job);
        PrimeThumbnail(job);
        _ = LoadMetadataAsync(job, includeThumbnail: true);
        RaiseCommands();
        return job;
    }

    private void RemoveJob(DownloadJob? job)
    {
        if (job is not null) { Jobs.Remove(job); Log("Eintrag entfernt.", "DEL"); RaiseCommands(); }
    }

    private void SkipJob(DownloadJob? job)
    {
        if (job is null || job != _currentJob) { Log("Skip nur für den aktuell laufenden Job möglich.", "WARN"); return; }
        _skipRequested = true; job.Detail = "Skip angefordert…"; Log("Skip angefordert: " + job.Url, "SKIP");
        _currentProcessCts?.Cancel();
    }

    private const string UrlsTemplate =
        "# Eine URL pro Zeile. Zeilen mit # werden ignoriert.\r\n" +
        "# Optional hinter der URL: Zeitbereich 00:00:10-00:02:30 und/oder mp3 für reinen Ton.\r\n";

    private void ImportUrls()
    {
        var file = SettingsService.ResolveDataFile(SettingsService.Current.UrlsFile, "urls.txt");
        if (!File.Exists(file))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, UrlsTemplate);
                Log("URLs-Datei angelegt – bitte URLs eintragen und erneut importieren: " + file, "WARN");
            }
            catch (Exception ex) { Log("URLs-Datei konnte nicht angelegt werden: " + ex.Message, "ERR"); }
            return;
        }
        var count = 0;
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var mode = parts.Skip(1).Any(p => p.Equals("mp3", StringComparison.OrdinalIgnoreCase)) ? "mp3" : "video";
                var sections = parts.Skip(1).Where(p => SectionRegex().IsMatch(p));
                CreateJob(parts[0], mode, string.Join(',', sections), "Best", mode == "video" ? "MP4" : "MP3", "Original", "CPU (Standard)");
                count++;
            }
            Log(IsRunning
                ? $"{count} Jobs für den nächsten Queue-Start importiert."
                : $"{count} Jobs importiert.",
                "IMPORT");
        }
        catch (Exception ex) { Log("Fehler Import: " + ex.Message, "ERR"); }
    }

    private async Task LoadMetadataAsync(DownloadJob job, bool includeThumbnail)
    {
        try
        {
            var entry = await EnsureMetadataAsync(job.Url, CookieSelection, includeThumbnail);
            if (entry.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title)) job.Title = title;
            if (entry.TryGetValue("thumbnail", out var thumbnail) && !string.IsNullOrWhiteSpace(thumbnail)) job.ThumbnailUrl = thumbnail;
        }
        catch (Exception ex) { Log($"Metadaten nicht geladen für {job.Url}: {ex.Message}", "META"); }
    }

    private void PrimeThumbnail(DownloadJob job)
    {
        var id = YouTubeId(job.Url);
        if (id is null) return;
        job.ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        _metadata[job.Url] = new Dictionary<string, string?> { ["id"] = id, ["thumbnail"] = job.ThumbnailUrl };
    }

    private async Task<Dictionary<string, string?>> EnsureMetadataAsync(string url, string cookieSelection, bool includeThumbnail)
    {
        if (!_metadata.TryGetValue(url, out var entry)) _metadata[url] = entry = [];
        if (!entry.ContainsKey("title") || !entry.ContainsKey("id") || includeThumbnail && !entry.ContainsKey("thumbnail"))
        {
            var fetched = await _ytDlp.FetchMetadataAsync(url, BuildCookieArgs(cookieSelection));
            foreach (var pair in fetched) if (!string.IsNullOrWhiteSpace(pair.Value)) entry[pair.Key] = pair.Value;
        }
        return entry;
    }

    private static string? YouTubeId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)) return uri.AbsolutePath.Trim('/').Split('/')[0];
        if (!uri.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase)) return null;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (!string.IsNullOrWhiteSpace(query["v"])) return query["v"];
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 2 && new[] { "shorts", "embed", "live", "v" }.Contains(parts[0]) ? parts[1] : null;
    }

    private List<string> BuildCookieArgs(string selection)
    {
        var settings = SettingsService.Current;
        if (selection.StartsWith("Browser:", StringComparison.Ordinal))
        {
            var browser = selection.Split(": ", 2)[1].ToLowerInvariant();
            var profile = string.Equals(browser, settings.CookieBrowser, StringComparison.OrdinalIgnoreCase)
                ? settings.CookieProfile.Trim()
                : string.Empty;
            return ["--cookies-from-browser", profile.Length > 0 ? $"{browser}:{profile}" : browser];
        }
        if (selection.StartsWith("Datei:", StringComparison.Ordinal))
        {
            var path = SettingsService.ResolveDataFile(settings.CookieFile, "cookies.txt");
            if (File.Exists(path)) return ["--cookies", path];
            Log("Warnung: Cookie-Datei nicht gefunden: " + path, "WARN");
        }
        return [];
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private async Task UpdateYtDlpAsync()
    {
        IsRunning = true; Log("Starte yt-dlp Update…", "SYS");
        try { await _ytDlp.UpdateAsync(line => Log(line, "UPDATE")); OnPropertyChanged(nameof(HasYtDlp)); Log("Update Vorgang beendet.", "SYS"); }
        finally { IsRunning = false; }
    }

    private async Task UpdateFfmpegAsync()
    {
        IsRunning = true; Log("Starte FFmpeg / FFplay / FFprobe Update…", "SYS");
        try
        {
            var progress = new Progress<string>(line => Log(line, "UPDATE"));
            var ok = await _ffmpeg.UpdateAsync(progress);
            OnPropertyChanged(nameof(HasFfmpeg));
            if (ok) Log($"FFmpeg bereit: {_ffmpeg.FfmpegPath}", "UPDATE");
            else if (_ffmpeg.IsAvailable) Log("FFmpeg Update unvollständig — siehe UPDATE-Zeilen oben.", "ERR");
            else Log("FFmpeg Update fehlgeschlagen.", "ERR");
            Log("Update Vorgang beendet.", "SYS");
        }
        finally { IsRunning = false; }
    }

    private async Task RunRawAsync()
    {
        if (string.IsNullOrWhiteSpace(RawCommand))
        {
            Log("Bitte zuerst einen vollständigen yt-dlp Befehl eingeben.", "WARN");
            return;
        }
        IReadOnlyList<string> tokens;
        try { tokens = Formatters.ParseCommandLine(RawCommand); }
        catch (FormatException ex) { Log("Befehl ungültig: " + ex.Message, "ERR"); return; }
        if (tokens.Count == 0) return;
        var executable = _ytDlp.ExecutablePath;
        var args = tokens.ToList();
        if (args[0].Equals("yt-dlp", StringComparison.OrdinalIgnoreCase) || args[0].Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
        else if (args[0].EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0])) { executable = args[0]; args.RemoveAt(0); }
        if (executable is null) { Log("yt-dlp.exe wurde nicht gefunden.", "ERR"); return; }
        var displayUrl = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? RawCommand;
        var job = CreateJob(displayUrl, "video", string.Empty, "—", "—", "Original", "CPU (Standard)");
        job.Title = displayUrl; job.Detail = "RAW | " + RawCommand; job.State = JobState.Running; job.CanSkip = true;
        IsRunning = true; _currentJob = job; _skipRequested = false; _currentProcessCts = new CancellationTokenSource();
        try
        {
            Log("RAW Befehl: " + RawCommand, "RAW");
            async Task Line(string line) { Log(line, "RAW"); await Task.CompletedTask; }
            var result = await ProcessRunner.RunStreamingAsync(executable, args, Line, Line, cancellationToken: _currentProcessCts.Token);
            if (_skipRequested) { job.State = JobState.Skipped; job.Detail = "RAW | Übersprungen"; }
            else if (result.ExitCode == 0) { job.State = JobState.Done; job.Detail = "RAW | 100% | Fertig"; job.Progress = 1; }
            else { job.State = JobState.Error; job.Detail = $"RAW | Fehler (Code {result.ExitCode})"; }
        }
        catch (Exception ex) { job.State = JobState.Error; job.Detail = "RAW | Fehler"; Log(ex.Message, "CRIT"); }
        finally { job.CanSkip = false; _currentJob = null; _currentProcessCts?.Dispose(); _currentProcessCts = null; IsRunning = false; }
    }

    private void RetryFailed()
    {
        foreach (var failed in _failedJobs.ToList())
        {
            var retry = failed.CloneForRetry(Jobs.Count + 1);
            Jobs.Add(retry);
            PrimeThumbnail(retry);
            _ = LoadMetadataAsync(retry, includeThumbnail: true);
        }
        _failedJobs.Clear(); RefreshFailed(); RaiseCommands();
    }

    private string GetOutputDirectory() => SettingsService.OutputDirectory(UseMega);
    private void Log(string message, string tag = "INFO")
    {
        var generation = Volatile.Read(ref _logGeneration);
        _pendingLogLines.Enqueue(new LogEntry(generation,
            $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}{Environment.NewLine}"));
        ScheduleLogFlush();
    }

    private void ScheduleLogFlush()
    {
        if (Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) != 0) return;
        _ = FlushLogAsync();
    }

    private async Task FlushLogAsync()
    {
        await Task.Delay(100).ConfigureAwait(false);
        var batch = new List<LogEntry>();
        while (batch.Count < 1000 && _pendingLogLines.TryDequeue(out var entry)) batch.Add(entry);

        void Publish()
        {
            var generation = Volatile.Read(ref _logGeneration);
            var lines = batch.Where(entry => entry.Generation == generation).Select(entry => entry.Line).ToArray();
            if (lines.Length == 0) return;
            lock (_logHistory)
            {
                foreach (var line in lines) _logHistory.Enqueue(line);
                while (_logHistory.Count > MaxLogLines) _logHistory.Dequeue();
            }
            OnPropertyChanged(nameof(LogText));
            LogLinesAppended?.Invoke(this, lines);
        }

        if (_uiDispatcher is null || _uiDispatcher.CheckAccess()) Publish();
        else _ = _uiDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Publish));
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        if (!_pendingLogLines.IsEmpty) ScheduleLogFlush();
    }

    private void ClearLog()
    {
        Interlocked.Increment(ref _logGeneration);
        while (_pendingLogLines.TryDequeue(out _)) { }
        lock (_logHistory) _logHistory.Clear();
        OnPropertyChanged(nameof(LogText));
        LogCleared?.Invoke(this, EventArgs.Empty);
    }

    private sealed record LogEntry(int Generation, string Line);
    private void AddFailed(DownloadJob job) { _failedJobs.Add(job); RefreshFailed(); }
    private void RefreshFailed() { OnPropertyChanged(nameof(FailedCount)); OnPropertyChanged(nameof(ErrorButtonText)); OnPropertyChanged(nameof(FailedJobsText)); RetryFailedCommand.RaiseCanExecuteChanged(); }
    private void RaiseCommands()
    {
        AddTimeSegmentCommand.RaiseCanExecuteChanged(); RemoveTimeSegmentCommand.RaiseCanExecuteChanged();
        AddJobCommand.RaiseCanExecuteChanged(); RemoveJobCommand.RaiseCanExecuteChanged(); SkipJobCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged(); StartCommand.RaiseCanExecuteChanged(); RawCommandCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged(); FfmpegUpdateCommand.RaiseCanExecuteChanged(); RetryFailedCommand.RaiseCanExecuteChanged();
    }

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}-\d{2}:\d{2}:\d{2}$")]
    private static partial Regex SectionRegex();
}
