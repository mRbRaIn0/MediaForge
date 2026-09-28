using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed class CutViewModel : EditorViewModelBase
{
    private string _startText = "00:00:00.000";
    private string _endText = "00:00:00.000";
    private double _inPoint = double.NaN;
    private double _outPoint = double.NaN;
    private string _outputName = string.Empty;
    private string? _outputFolder;
    private bool _exact;
    private string _sizeEstimate = string.Empty;

    public CutViewModel(FfmpegService ffmpeg, Action back) : base(ffmpeg, back)
    {
        ChooseFileCommand = new AsyncRelayCommand(_ => ChooseFileAsync(), _ => !IsBusy);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsBusy);
        SetInCommand = new RelayCommand(_ => SetIn(), _ => HasMedia);
        SetOutCommand = new RelayCommand(_ => SetOut(), _ => HasMedia);
        ApplyTimesCommand = new RelayCommand(_ => ApplyTimes(), _ => HasMedia);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => HasMedia && HasFfmpeg && !IsBusy);
    }

    public AsyncRelayCommand ChooseFileCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand SetInCommand { get; }
    public RelayCommand SetOutCommand { get; }
    public RelayCommand ApplyTimesCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public string StartText { get => _startText; set => SetProperty(ref _startText, value); }
    public string EndText { get => _endText; set => SetProperty(ref _endText, value); }
    public double InPoint { get => _inPoint; private set => SetProperty(ref _inPoint, value); }
    public double OutPoint { get => _outPoint; private set => SetProperty(ref _outPoint, value); }
    public string OutputName { get => _outputName; set => SetProperty(ref _outputName, value); }
    public string OutputFolder { get => _outputFolder ?? "(Quellordner)"; set => SetProperty(ref _outputFolder, value); }
    public bool Exact { get => _exact; set => SetProperty(ref _exact, value); }
    public string SizeEstimate { get => _sizeEstimate; private set => SetProperty(ref _sizeEstimate, value); }

    protected override Task OnMediaLoadedAsync()
    {
        InPoint = 0;
        OutPoint = Duration;
        StartText = Formatters.Time(0);
        EndText = Formatters.Time(Duration);
        OutputName = $"{Path.GetFileNameWithoutExtension(SourcePath)}_cut{Path.GetExtension(SourcePath)}";
        UpdateSizeEstimate();
        return Task.CompletedTask;
    }

    private async Task ChooseFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "Video wählen", Filter = "Videodateien|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.vob;*.ogg|Alle Dateien|*.*" };
        if (dialog.ShowDialog() == true) await LoadMediaAsync(dialog.FileName);
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Zielordner wählen" };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    private void SetIn()
    {
        InPoint = Position;
        if (InPoint > OutPoint) OutPoint = Duration;
        StartText = Formatters.Time(InPoint); EndText = Formatters.Time(OutPoint); UpdateSizeEstimate();
    }

    private void SetOut()
    {
        OutPoint = Position;
        if (OutPoint < InPoint) InPoint = 0;
        StartText = Formatters.Time(InPoint); EndText = Formatters.Time(OutPoint); UpdateSizeEstimate();
    }

    private void ApplyTimes()
    {
        var start = Formatters.ParseTime(StartText);
        var end = Formatters.ParseTime(EndText);
        if (start is null || end is null || start < 0 || end > Duration + .5 || start >= end)
        {
            System.Windows.MessageBox.Show("Bitte gültige Start-/Endzeiten eingeben (Start < Ende).", "Ungültige Zeiten", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        InPoint = start.Value; OutPoint = Math.Min(end.Value, Duration); Position = InPoint; UpdateSizeEstimate();
    }

    private void UpdateSizeEstimate()
    {
        if (Info?.Size is null || Duration <= 0) { SizeEstimate = string.Empty; return; }
        var estimate = (long)(Info.Size.Value * Math.Max(0, OutPoint - InPoint) / Duration);
        SizeEstimate = $"Vorher {Formatters.Size(Info.Size)}  →  Nachher ~{Formatters.Size(estimate)}";
    }

    private async Task ExportAsync()
    {
        if (SourcePath is null || OutPoint <= InPoint) return;
        var directory = _outputFolder ?? Path.GetDirectoryName(SourcePath)!;
        var name = string.IsNullOrWhiteSpace(OutputName) ? $"{Path.GetFileNameWithoutExtension(SourcePath)}_cut{Path.GetExtension(SourcePath)}" : OutputName;
        var output = Formatters.UniquePath(Path.Combine(directory, name));
        var duration = OutPoint - InPoint;
        var args = new List<string> { "-y", "-ss", FfmpegService.F(InPoint), "-i", SourcePath, "-t", FfmpegService.F(duration) };
        if (Exact) args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "192k"]);
        else args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
        args.Add(output);
        IsBusy = true; Progress = 0; Status = "Exportiere…";
        try
        {
            var code = await Ffmpeg.RunAsync(args, duration, new Progress<double>(p => Progress = p));
            if (code == 0 && File.Exists(output)) { Progress = 1; Status = $"Fertig: {Path.GetFileName(output)} ({Formatters.Size(new FileInfo(output).Length)})"; }
            else { Progress = 0; Status = "Fehler beim Export."; }
        }
        catch (Exception ex) { Status = "Fehler: " + ex.Message; }
        finally { IsBusy = false; ExportCommand.RaiseCanExecuteChanged(); }
    }
}
