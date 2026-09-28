using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Data;
using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

/// <summary>Rückmeldung des Marker-Dialogs an das ViewModel.</summary>
public enum MarkerDialogResult { Cancelled, Saved, Deleted }

/// <summary>
/// Der Editor: Zeitleiste mit Markern, eine Schnittliste aus Abschnitten der Quelldatei
/// und der Export daraus. Die Marker liegen in einer JSON-Datei neben dem Video.
/// </summary>
public sealed partial class EditorViewModel : EditorViewModelBase
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private MediaSidecar? _sidecar;
    private MediaMarker? _selectedMarker;
    private EditorClip? _selectedClip;
    private string _markerFilter = string.Empty;
    private string _outputName = string.Empty;
    private string? _outputFolder;
    private bool _exact;
    private string _saveStatus = "Noch keine Datei geladen.";
    private bool _suppressSave = true;

    public EditorViewModel(FfmpegService ffmpeg, Action back) : base(ffmpeg, back)
    {
        ChooseFileCommand = new AsyncRelayCommand(_ => ChooseFileAsync(), _ => !IsBusy);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsBusy);
        AddPointMarkerCommand = new RelayCommand(_ => AddMarker(MarkerKind.Point), _ => HasMedia);
        AddRangeMarkerCommand = new RelayCommand(_ => AddMarker(MarkerKind.Range), _ => HasMedia);
        EditMarkerCommand = new RelayCommand(p => EditMarker(p as MediaMarker ?? SelectedMarker), _ => HasMedia);
        DeleteMarkerCommand = new RelayCommand(p => DeleteMarker(p as MediaMarker ?? SelectedMarker), _ => HasMedia);
        JumpToMarkerCommand = new RelayCommand(p => JumpTo(p as MediaMarker ?? SelectedMarker), _ => HasMedia);
        SaveSidecarCommand = new AsyncRelayCommand(_ => SaveSidecarAsync(manual: true), _ => Sidecar is not null);
        OpenSidecarFolderCommand = new RelayCommand(_ => OpenSidecarFolder(), _ => Sidecar is not null);
        SplitClipCommand = new RelayCommand(_ => SplitAtPlayhead(), _ => HasMedia);
        RemoveClipCommand = new RelayCommand(p => RemoveClip(p as EditorClip ?? SelectedClip), _ => Clips.Count > 0);
        MoveClipUpCommand = new RelayCommand(p => MoveClip(p as EditorClip ?? SelectedClip, -1), _ => Clips.Count > 1);
        MoveClipDownCommand = new RelayCommand(p => MoveClip(p as EditorClip ?? SelectedClip, 1), _ => Clips.Count > 1);
        ResetClipsCommand = new RelayCommand(_ => ResetClips(), _ => HasMedia);
        ClearClipsCommand = new RelayCommand(_ => { Clips.Clear(); Renumber(); }, _ => Clips.Count > 0);
        ClipFromMarkerCommand = new RelayCommand(p => ClipFromMarker(p as MediaMarker ?? SelectedMarker),
            p => (p as MediaMarker ?? SelectedMarker)?.IsRange == true);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => HasMedia && HasFfmpeg && !IsBusy && Clips.Count > 0);
        ExportMarkerCommand = new AsyncRelayCommand(p => ExportMarkerAsync(p as MediaMarker ?? SelectedMarker),
            p => HasMedia && HasFfmpeg && !IsBusy && (p as MediaMarker ?? SelectedMarker)?.IsRange == true);

        CollectionViewSource.GetDefaultView(Markers).Filter = item => item is not MediaMarker marker || marker.Matches(MarkerFilter);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Position)) OnPropertyChanged(nameof(PositionText)); };
        Markers.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(MarkerSummary)); OnPropertyChanged(nameof(HasMarkers)); };
        Clips.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ClipSummary));
            OnPropertyChanged(nameof(HasClips));
            ExportCommand.RaiseCanExecuteChanged();
            RemoveClipCommand.RaiseCanExecuteChanged();
            ClearClipsCommand.RaiseCanExecuteChanged();
            MoveClipUpCommand.RaiseCanExecuteChanged();
            MoveClipDownCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>Öffnet den Marker-Dialog; wird von der Ansicht gesetzt (Marker, ist neu) → Ergebnis.</summary>
    public Func<MediaMarker, bool, MarkerDialogResult>? EditMarkerDialog { get; set; }

    /// <summary>Bittet die Ansicht, Player und Vorschau auf die Zeit zu setzen.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Signalisiert der Zeitleiste, dass Marker oder Clips neu gezeichnet werden müssen.</summary>
    public event Action? TimelineInvalidated;

    public ObservableCollection<MediaMarker> Markers { get; } = [];
    public ObservableCollection<EditorClip> Clips { get; } = [];
    public string[] Categories { get; } = MarkerCategories.Suggestions;

    public AsyncRelayCommand ChooseFileCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand AddPointMarkerCommand { get; }
    public RelayCommand AddRangeMarkerCommand { get; }
    public RelayCommand EditMarkerCommand { get; }
    public RelayCommand DeleteMarkerCommand { get; }
    public RelayCommand JumpToMarkerCommand { get; }
    public AsyncRelayCommand SaveSidecarCommand { get; }
    public RelayCommand OpenSidecarFolderCommand { get; }
    public RelayCommand SplitClipCommand { get; }
    public RelayCommand RemoveClipCommand { get; }
    public RelayCommand MoveClipUpCommand { get; }
    public RelayCommand MoveClipDownCommand { get; }
    public RelayCommand ResetClipsCommand { get; }
    public RelayCommand ClearClipsCommand { get; }
    public RelayCommand ClipFromMarkerCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ExportMarkerCommand { get; }

    public MediaSidecar? Sidecar
    {
        get => _sidecar;
        private set
        {
            var previous = _sidecar;
            if (!SetProperty(ref _sidecar, value)) return;
            if (previous is not null) previous.PropertyChanged -= Sidecar_PropertyChanged;
            if (value is not null) value.PropertyChanged += Sidecar_PropertyChanged;
            OnPropertyChanged(nameof(SidecarPathText));
            OnPropertyChanged(nameof(HasSidecar));
            SaveSidecarCommand.RaiseCanExecuteChanged();
            OpenSidecarFolderCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Änderungen an Titel, Beschreibung oder Datei-Tags landen sofort in der JSON-Datei.</summary>
    private void Sidecar_PropertyChanged(object? sender, PropertyChangedEventArgs e) => _ = SaveSidecarAsync();

    public MediaMarker? SelectedMarker
    {
        get => _selectedMarker;
        set
        {
            if (!SetProperty(ref _selectedMarker, value)) return;
            OnPropertyChanged(nameof(HasSelectedMarker));
            ClipFromMarkerCommand.RaiseCanExecuteChanged();
            ExportMarkerCommand.RaiseCanExecuteChanged();
        }
    }

    public EditorClip? SelectedClip
    {
        get => _selectedClip;
        set
        {
            var previous = _selectedClip;
            if (!SetProperty(ref _selectedClip, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            TimelineInvalidated?.Invoke();
        }
    }

    /// <summary>Volltextsuche über Titel, Notiz, Kategorie, Tags und Zeit.</summary>
    public string MarkerFilter
    {
        get => _markerFilter;
        set { if (SetProperty(ref _markerFilter, value)) CollectionViewSource.GetDefaultView(Markers).Refresh(); }
    }

    public string OutputName { get => _outputName; set => SetProperty(ref _outputName, value); }
    public string OutputFolder { get => _outputFolder ?? "(Quellordner)"; set => SetProperty(ref _outputFolder, value); }
    public bool Exact { get => _exact; set => SetProperty(ref _exact, value); }
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }

    public bool HasSidecar => Sidecar is not null;
    public bool HasMarkers => Markers.Count > 0;
    public bool HasClips => Clips.Count > 0;
    public bool HasSelectedMarker => SelectedMarker is not null;
    public long DurationMs => Formatters.ToMs(Duration);
    public string PositionText => Formatters.TimeFromMs(Formatters.ToMs(Position), withMilliseconds: true);
    public string SidecarPathText => Sidecar?.SidecarPath ?? "—";

    public string MarkerSummary =>
        Markers.Count == 0 ? "noch keine Marker" : $"{Markers.Count(m => !m.IsRange)} Punkte · {Markers.Count(m => m.IsRange)} Bereiche";

    public string ClipSummary
    {
        get
        {
            if (Clips.Count == 0) return "leer — nichts zu exportieren";
            var total = Clips.Sum(c => c.DurationMs);
            return $"{Clips.Count} Clip{(Clips.Count == 1 ? string.Empty : "s")} · {Formatters.TimeFromMs(total)}";
        }
    }

    protected override async Task OnMediaLoadedAsync()
    {
        _suppressSave = true;
        SelectedMarker = null;
        SelectedClip = null;
        Markers.Clear();
        Clips.Clear();
        var sidecar = await SidecarService.LoadAsync(SourcePath!, Info);
        foreach (var marker in sidecar.Markers) Markers.Add(marker);
        Sidecar = sidecar;
        SaveStatus = sidecar.Existed
            ? $"{Markers.Count} Marker geladen aus {Path.GetFileName(sidecar.SidecarPath)}"
            : "Neu — die Sidecar-Datei entsteht beim ersten Speichern.";
        ResetClips();
        OutputName = $"{Path.GetFileNameWithoutExtension(SourcePath)}_edit{Path.GetExtension(SourcePath)}";
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(MarkerSummary));
        TimelineInvalidated?.Invoke();
        _suppressSave = false;
    }

    private async Task ChooseFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Video wählen",
            Filter = "Videodateien|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.vob;*.ogg|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() == true) await LoadMediaAsync(dialog.FileName);
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Zielordner wählen" };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    // ---------------------------------------------------------------- Marker

    /// <summary>Legt einen Marker an der Abspielposition an und öffnet dafür den Dialog.</summary>
    private void AddMarker(MarkerKind kind)
    {
        if (!HasMedia) return;
        var start = Formatters.ToMs(Position);
        var marker = new MediaMarker { Kind = kind, StartMs = start };
        marker.EndMs = kind == MarkerKind.Range ? Math.Min(Math.Max(DurationMs, start), start + 10_000) : start;
        if (EditMarkerDialog is not null && EditMarkerDialog(marker, true) != MarkerDialogResult.Saved) return;
        AddMarker(marker);
    }

    /// <summary>Nimmt einen fertigen Marker in die Liste auf, sortiert und speichert.</summary>
    public void AddMarker(MediaMarker marker)
    {
        Markers.Add(marker);
        SidecarService.Sort(Markers);
        SelectedMarker = marker;
        TimelineInvalidated?.Invoke();
        _ = SaveSidecarAsync();
    }

    private void EditMarker(MediaMarker? marker)
    {
        if (marker is null || EditMarkerDialog is null) return;
        var draft = marker.Clone();
        var result = EditMarkerDialog(draft, false);
        if (result == MarkerDialogResult.Deleted) { DeleteMarker(marker); return; }
        if (result != MarkerDialogResult.Saved) return;
        marker.CopyFrom(draft);
        marker.UpdatedAt = DateTimeOffset.UtcNow;
        SidecarService.Sort(Markers);
        TimelineInvalidated?.Invoke();
        _ = SaveSidecarAsync();
    }

    private void DeleteMarker(MediaMarker? marker)
    {
        if (marker is null || !Markers.Remove(marker)) return;
        if (ReferenceEquals(SelectedMarker, marker)) SelectedMarker = null;
        TimelineInvalidated?.Invoke();
        _ = SaveSidecarAsync();
    }

    private void JumpTo(MediaMarker? marker)
    {
        if (marker is null) return;
        SelectedMarker = marker;
        SeekTo(marker.StartSeconds);
    }

    /// <summary>Setzt Abspielkopf, Player und Vorschau auf eine Zeit.</summary>
    public void SeekTo(double seconds)
    {
        Position = Math.Clamp(seconds, 0, Math.Max(0, Duration));
        SeekRequested?.Invoke(Position);
    }

    // ------------------------------------------------------------ Sidecar-IO

    /// <summary>Schreibt Marker und Kopfdaten in die JSON-Datei neben dem Video.</summary>
    public async Task SaveSidecarAsync(bool manual = false)
    {
        if (Sidecar is null || (_suppressSave && !manual)) return;
        await _saveLock.WaitAsync();
        try
        {
            var sidecar = Sidecar;
            sidecar.DurationMs = DurationMs > 0 ? DurationMs : sidecar.DurationMs;
            sidecar.Markers.Clear();
            foreach (var marker in Markers) sidecar.Markers.Add(marker);
            await SidecarService.SaveAsync(sidecar);
            SaveStatus = $"Gespeichert {DateTime.Now:HH:mm:ss} · {Markers.Count} Marker";
        }
        catch (Exception ex)
        {
            SaveStatus = "Speichern fehlgeschlagen: " + ex.Message;
        }
        finally { _saveLock.Release(); }
    }

    private void OpenSidecarFolder()
    {
        if (Sidecar is null) return;
        var path = File.Exists(Sidecar.SidecarPath) ? Sidecar.SidecarPath : Sidecar.MediaPath;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SaveStatus = "Ordner konnte nicht geöffnet werden: " + ex.Message; }
    }

    // ------------------------------------------------------------ Schnitte

    private void ResetClips()
    {
        Clips.Clear();
        if (DurationMs > 0) Clips.Add(new EditorClip(0, DurationMs));
        Renumber();
    }

    /// <summary>Teilt den Clip unter dem Abspielkopf in zwei Abschnitte.</summary>
    private void SplitAtPlayhead()
    {
        var at = Formatters.ToMs(Position);
        var clip = Clips.FirstOrDefault(c => c.Contains(at));
        if (clip is null) { Status = "An dieser Position liegt kein Clip zum Teilen."; return; }
        var index = Clips.IndexOf(clip);
        var tail = new EditorClip(at, clip.EndMs);
        clip.EndMs = at;
        Clips.Insert(index + 1, tail);
        SelectedClip = tail;
        Renumber();
    }

    private void RemoveClip(EditorClip? clip)
    {
        if (clip is null || !Clips.Remove(clip)) return;
        if (ReferenceEquals(SelectedClip, clip)) SelectedClip = null;
        Renumber();
    }

    private void MoveClip(EditorClip? clip, int offset)
    {
        if (clip is null) return;
        var index = Clips.IndexOf(clip);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Clips.Count) return;
        Clips.Move(index, target);
        Renumber();
    }

    /// <summary>Hängt den Bereich eines Marker an die Schnittliste an — so entsteht z. B. eine Highlight-Reihe.</summary>
    private void ClipFromMarker(MediaMarker? marker)
    {
        if (marker is null) return;
        if (!marker.IsRange) { Status = "Nur Bereich-Marker lassen sich als Clip übernehmen."; return; }
        var clip = new EditorClip(marker.StartMs, Math.Max(marker.StartMs, marker.EndMs), marker.DisplayTitle);
        Clips.Add(clip);
        SelectedClip = clip;
        Renumber();
        Status = $"Clip aus „{marker.DisplayTitle}“ übernommen.";
    }

    private void Renumber()
    {
        for (var i = 0; i < Clips.Count; i++) Clips[i].Number = i + 1;
        OnPropertyChanged(nameof(ClipSummary));
        TimelineInvalidated?.Invoke();
    }

    // -------------------------------------------------------------- Export

    private async Task ExportAsync()
    {
        if (SourcePath is null || Clips.Count == 0) return;
        var segments = Clips.Where(c => c.DurationMs > 0).Select(c => (c.StartSeconds, c.EndSeconds)).ToList();
        if (segments.Count == 0) { Status = "Alle Clips sind leer."; return; }
        var directory = _outputFolder ?? Path.GetDirectoryName(SourcePath)!;
        var name = string.IsNullOrWhiteSpace(OutputName)
            ? $"{Path.GetFileNameWithoutExtension(SourcePath)}_edit{Path.GetExtension(SourcePath)}"
            : OutputName;
        await RunExportAsync(segments, Path.Combine(directory, name));
    }

    private async Task ExportMarkerAsync(MediaMarker? marker)
    {
        if (SourcePath is null || marker is null) return;
        if (!marker.IsRange) { Status = "Nur Bereich-Marker lassen sich einzeln exportieren."; return; }
        var directory = _outputFolder ?? Path.GetDirectoryName(SourcePath)!;
        var label = SafeName(marker.DisplayTitle);
        var name = $"{Path.GetFileNameWithoutExtension(SourcePath)}_{label}{Path.GetExtension(SourcePath)}";
        await RunExportAsync([(marker.StartSeconds, marker.EndSeconds)], Path.Combine(directory, name));
    }

    private async Task RunExportAsync(IReadOnlyList<(double Start, double End)> segments, string target)
    {
        var output = Formatters.UniquePath(target);
        var total = Math.Max(.001, segments.Sum(s => s.End - s.Start));
        IsBusy = true; Progress = 0; Status = segments.Count == 1 ? "Exportiere…" : $"Exportiere {segments.Count} Clips…";
        var temp = Path.Combine(Path.GetTempPath(), "mediaforge_edit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ok = segments.Count == 1
                ? await TrimAsync(segments[0], output, total, 0)
                : await TrimAndConcatAsync(segments, output, temp, total);
            if (!ok && segments.Count > 1) ok = await FullEncodeAsync(segments, output, total);
            if (ok && File.Exists(output))
            {
                Progress = 1;
                Status = $"Fertig: {Path.GetFileName(output)} ({Formatters.Size(new FileInfo(output).Length)})";
            }
            else { Progress = 0; Status = "Export fehlgeschlagen."; }
        }
        catch (Exception ex) { Progress = 0; Status = "Fehler: " + ex.Message; }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* Aufräumen ist optional. */ }
            IsBusy = false;
            ExportCommand.RaiseCanExecuteChanged();
            ExportMarkerCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task<bool> TrimAsync((double Start, double End) segment, string output, double total, double done)
    {
        var duration = Math.Max(.001, segment.End - segment.Start);
        var args = new List<string> { "-y", "-ss", FfmpegService.F(segment.Start), "-i", SourcePath!, "-t", FfmpegService.F(duration) };
        args.AddRange(Exact
            ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "192k"]
            : ["-c", "copy", "-avoid_negative_ts", "make_zero"]);
        args.Add(output);
        var code = await Ffmpeg.RunAsync(args, duration, new Progress<double>(p => Progress = Math.Min(.98, (done + p * duration) / total)));
        return code == 0 && File.Exists(output);
    }

    private async Task<bool> TrimAndConcatAsync(IReadOnlyList<(double Start, double End)> segments, string output, string temp, double total)
    {
        Directory.CreateDirectory(temp);
        var extension = Path.GetExtension(output);
        var files = new List<string>();
        var done = 0d;
        for (var i = 0; i < segments.Count; i++)
        {
            var file = Path.Combine(temp, $"clip_{i:000}{extension}");
            if (!await TrimAsync(segments[i], file, total, done)) return false;
            files.Add(file);
            done += segments[i].End - segments[i].Start;
            Progress = Math.Min(.98, done / total);
        }
        Status = "Füge Clips zusammen…";
        var list = Path.Combine(temp, "list.txt");
        await File.WriteAllLinesAsync(list, files.Select(p => $"file '{p.Replace('\\', '/')}'"));
        var args = new List<string> { "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", output };
        return await Ffmpeg.RunAsync(args, total) == 0 && File.Exists(output);
    }

    /// <summary>Fallback, wenn sich die Clips nicht verlustfrei aneinanderhängen lassen.</summary>
    private async Task<bool> FullEncodeAsync(IReadOnlyList<(double Start, double End)> segments, string output, double total)
    {
        if (SourcePath is null || Info is null) return false;
        Status = "Fallback: Clips werden neu kodiert…";
        Progress = 0;
        var parts = new List<string>();
        var videoLabels = new List<string>();
        var audioLabels = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            if (Info.HasVideo)
            {
                parts.Add($"[0:v]trim=start={FfmpegService.F(start)}:end={FfmpegService.F(end)},setpts=PTS-STARTPTS[v{i}]");
                videoLabels.Add($"[v{i}]");
            }
            if (Info.HasAudio)
            {
                parts.Add($"[0:a]atrim=start={FfmpegService.F(start)}:end={FfmpegService.F(end)},asetpts=PTS-STARTPTS[a{i}]");
                audioLabels.Add($"[a{i}]");
            }
        }
        if (Info.HasVideo) parts.Add(string.Concat(videoLabels) + $"concat=n={segments.Count}:v=1:a=0[vout]");
        if (Info.HasAudio) parts.Add(string.Concat(audioLabels) + $"concat=n={segments.Count}:v=0:a=1[aout]");
        var args = new List<string> { "-y", "-i", SourcePath, "-filter_complex", string.Join(';', parts) };
        if (Info.HasVideo) args.AddRange(["-map", "[vout]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", Info.PixelFormat]);
        if (Info.HasAudio) args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        return await Ffmpeg.RunAsync(args, total, new Progress<double>(p => Progress = Math.Min(.99, p))) == 0 && File.Exists(output);
    }

    private static string SafeName(string value)
    {
        var cleaned = InvalidNameCharacters().Replace(value.Trim(), "_").Trim('_');
        if (cleaned.Length > 40) cleaned = cleaned[..40];
        return cleaned.Length == 0 ? "clip" : cleaned;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\-_. ]+")]
    private static partial Regex InvalidNameCharacters();
}
