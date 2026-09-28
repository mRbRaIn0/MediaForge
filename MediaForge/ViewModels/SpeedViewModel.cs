using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed class SpeedViewModel : EditorViewModelBase
{
    private string _pendingStart = "00:00:00.000";
    private string _pendingEnd = "00:00:10.000";
    private string _factor = "2";
    private string _outputName = string.Empty;
    private string? _outputFolder;
    private double _pendingIn = double.NaN;
    private double _pendingOut = double.NaN;

    public SpeedViewModel(FfmpegService ffmpeg, Action back) : base(ffmpeg, back)
    {
        ChooseFileCommand = new AsyncRelayCommand(_ => ChooseFileAsync(), _ => !IsBusy);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsBusy);
        SetStartCommand = new RelayCommand(_ => SetStart(), _ => HasMedia);
        SetEndCommand = new RelayCommand(_ => SetEnd(), _ => HasMedia);
        AddZoneCommand = new RelayCommand(_ => AddZone(), _ => HasMedia && !IsBusy);
        RemoveZoneCommand = new RelayCommand(p => RemoveZone(p as SpeedZone), _ => !IsBusy);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => HasMedia && Zones.Count > 0 && HasFfmpeg && !IsBusy);
    }

    public ObservableCollection<SpeedZone> Zones { get; } = [];
    public string[] Factors { get; } = ["0.25", "0.5", "0.75", "1.25", "1.5", "2", "3", "4"];
    public AsyncRelayCommand ChooseFileCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand SetStartCommand { get; }
    public RelayCommand SetEndCommand { get; }
    public RelayCommand AddZoneCommand { get; }
    public RelayCommand RemoveZoneCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public string PendingStart { get => _pendingStart; set { if (SetProperty(ref _pendingStart, value)) UpdatePendingMarkers(); } }
    public string PendingEnd { get => _pendingEnd; set { if (SetProperty(ref _pendingEnd, value)) UpdatePendingMarkers(); } }
    public string Factor { get => _factor; set => SetProperty(ref _factor, value); }
    public string OutputName { get => _outputName; set => SetProperty(ref _outputName, value); }
    public string OutputFolder { get => _outputFolder ?? "(Quellordner)"; set => SetProperty(ref _outputFolder, value); }
    public double PendingIn { get => _pendingIn; private set => SetProperty(ref _pendingIn, value); }
    public double PendingOut { get => _pendingOut; private set => SetProperty(ref _pendingOut, value); }

    protected override Task OnMediaLoadedAsync()
    {
        Zones.Clear();
        PendingStart = Formatters.Time(0);
        PendingEnd = Formatters.Time(Math.Min(10, Duration));
        OutputName = $"{Path.GetFileNameWithoutExtension(SourcePath)}_speed{Path.GetExtension(SourcePath)}";
        UpdatePendingMarkers();
        return Task.CompletedTask;
    }

    private async Task ChooseFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "Video/Audio wählen", Filter = "Medien|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.vob;*.ogg;*.mp3;*.aac;*.wav;*.flac;*.m4a;*.opus;*.wma|Alle Dateien|*.*" };
        if (dialog.ShowDialog() == true) await LoadMediaAsync(dialog.FileName);
    }

    private void ChooseFolder() { var dialog = new OpenFolderDialog { Title = "Zielordner wählen" }; if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName; }
    private void SetStart() => PendingStart = Formatters.Time(Position);
    private void SetEnd() => PendingEnd = Formatters.Time(Position);
    private void UpdatePendingMarkers()
    {
        PendingIn = Formatters.ParseTime(PendingStart) ?? 0;
        PendingOut = Formatters.ParseTime(PendingEnd) ?? 0;
    }

    private void AddZone()
    {
        var start = Formatters.ParseTime(PendingStart);
        var end = Formatters.ParseTime(PendingEnd);
        if (!double.TryParse(Factor, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)) factor = 0;
        if (start is null || end is null || start < 0 || end > Duration + .5 || start >= end)
        {
            System.Windows.MessageBox.Show("Bitte gültigen Start < Ende innerhalb der Dauer eingeben.", "Ungültig", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        if (factor <= 0)
        {
            System.Windows.MessageBox.Show("Faktor muss > 0 sein (z. B. 0.5 oder 2).", "Ungültig", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        if (Zones.Any(z => start < z.End && end > z.Start))
        {
            System.Windows.MessageBox.Show("Zonen dürfen sich nicht überlappen.", "Überlappung", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        var zone = new SpeedZone { Start = start.Value, End = Math.Min(end.Value, Duration), Factor = factor };
        var index = 0;
        while (index < Zones.Count && Zones[index].Start < zone.Start) index++;
        Zones.Insert(index, zone);
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void RemoveZone(SpeedZone? zone) { if (zone is not null) Zones.Remove(zone); ExportCommand.RaiseCanExecuteChanged(); }

    public double FactorAt(double position)
    {
        var zone = Zones.FirstOrDefault(z => position >= z.Start && position < z.End);
        return zone?.Factor ?? 1;
    }

    private List<SpeedSegment> BuildSegments()
    {
        var result = new List<SpeedSegment>();
        var cursor = 0d;
        foreach (var zone in Zones.OrderBy(z => z.Start))
        {
            if (zone.Start > cursor + .001) result.Add(new SpeedSegment(cursor, zone.Start, 1));
            result.Add(new SpeedSegment(zone.Start, zone.End, zone.Factor));
            cursor = zone.End;
        }
        if (cursor < Duration - .001) result.Add(new SpeedSegment(cursor, Duration, 1));
        return result;
    }

    private async Task ExportAsync()
    {
        if (SourcePath is null) return;
        var directory = _outputFolder ?? Path.GetDirectoryName(SourcePath)!;
        var name = string.IsNullOrWhiteSpace(OutputName) ? $"{Path.GetFileNameWithoutExtension(SourcePath)}_speed{Path.GetExtension(SourcePath)}" : OutputName;
        var output = Formatters.UniquePath(Path.Combine(directory, name));
        var temp = Path.Combine(Path.GetTempPath(), "mediaforge_speed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        IsBusy = true; Progress = 0; Status = "Verarbeite Segmente…";
        try
        {
            var segments = BuildSegments();
            var total = Math.Max(1, segments.Sum(s => s.End - s.Start));
            var files = new List<string>();
            var done = 0d;
            var ok = true;
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var extension = Path.GetExtension(output);
                var file = Path.Combine(temp, $"seg_{i:000}{extension}");
                var duration = segment.End - segment.Start;
                var args = new List<string> { "-y", "-ss", FfmpegService.F(segment.Start), "-i", SourcePath, "-t", FfmpegService.F(duration) };
                if (Math.Abs(segment.Factor - 1) < 1e-6) args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
                else
                {
                    if (Info?.HasVideo == true) args.AddRange(["-filter:v", $"setpts=PTS/{FfmpegService.F(segment.Factor)}", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", Info.PixelFormat]);
                    if (Info?.HasAudio == true) args.AddRange(["-filter:a", Formatters.Atempo(segment.Factor), "-c:a", "aac", "-b:a", "192k"]);
                }
                args.Add(file);
                var before = done;
                var code = await Ffmpeg.RunAsync(args, Math.Abs(segment.Factor - 1) > 1e-6 ? duration / segment.Factor : duration,
                    new Progress<double>(p => Progress = Math.Min(.98, (before + p * duration) / total)));
                if (code != 0 || !File.Exists(file)) { ok = false; break; }
                files.Add(file); done += duration; Progress = Math.Min(.98, done / total);
            }
            if (ok && files.Count > 0) ok = await ConcatAsync(files, output, temp, total);
            if (!ok) ok = await FullEncodeAsync(output, segments, total);
            if (ok && File.Exists(output)) { Progress = 1; Status = $"Fertig: {Path.GetFileName(output)} ({Formatters.Size(new FileInfo(output).Length)})"; }
            else { Progress = 0; Status = "Export fehlgeschlagen."; }
        }
        catch (Exception ex) { Progress = 0; Status = "Fehler: " + ex.Message; }
        finally { try { Directory.Delete(temp, true); } catch { } IsBusy = false; ExportCommand.RaiseCanExecuteChanged(); }
    }

    private async Task<bool> ConcatAsync(IReadOnlyList<string> files, string output, string temp, double total)
    {
        var list = Path.Combine(temp, "list.txt");
        await File.WriteAllLinesAsync(list, files.Select(p => $"file '{p.Replace('\\', '/')}'"));
        var args = new List<string> { "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", output };
        return await Ffmpeg.RunAsync(args, total) == 0;
    }

    private async Task<bool> FullEncodeAsync(string output, IReadOnlyList<SpeedSegment> segments, double total)
    {
        if (SourcePath is null || Info is null) return false;
        Status = "Fallback: voller Re-Encode…";
        var parts = new List<string>(); var videoLabels = new List<string>(); var audioLabels = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i]; var factor = FfmpegService.F(s.Factor);
            if (Info.HasVideo) { parts.Add($"[0:v]trim=start={FfmpegService.F(s.Start)}:end={FfmpegService.F(s.End)},setpts=(PTS-STARTPTS)/{factor}[v{i}]"); videoLabels.Add($"[v{i}]"); }
            if (Info.HasAudio) { parts.Add($"[0:a]atrim=start={FfmpegService.F(s.Start)}:end={FfmpegService.F(s.End)},asetpts=PTS-STARTPTS,{Formatters.Atempo(s.Factor)}[a{i}]"); audioLabels.Add($"[a{i}]"); }
        }
        if (Info.HasVideo) parts.Add(string.Concat(videoLabels) + $"concat=n={segments.Count}:v=1:a=0[vout]");
        if (Info.HasAudio) parts.Add(string.Concat(audioLabels) + $"concat=n={segments.Count}:v=0:a=1[aout]");
        var args = new List<string> { "-y", "-i", SourcePath, "-filter_complex", string.Join(';', parts) };
        if (Info.HasVideo) args.AddRange(["-map", "[vout]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", Info.PixelFormat]);
        if (Info.HasAudio) args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        return await Ffmpeg.RunAsync(args, total, new Progress<double>(p => Progress = Math.Min(.99, p))) == 0;
    }
}
