using System.Collections.ObjectModel;
using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed class ConvertViewModel : ObservableObject, IToolAware
{
    private readonly FfmpegService _ffmpeg;
    private string _format = "mp4";
    private string _encoder = "GPU (NVIDIA NVENC)";
    private bool _onlyBroken;
    private bool _useCustomFolder;
    private string? _outputFolder;
    private bool _isRunning;
    private string _logText = string.Empty;

    public ConvertViewModel(FfmpegService ffmpeg, Action back)
    {
        _ffmpeg = ffmpeg;
        BackCommand = new RelayCommand(_ => back());
        AddFilesCommand = new AsyncRelayCommand(_ => AddFilesAsync(), _ => !IsRunning);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsRunning);
        RemoveCommand = new RelayCommand(p => Remove(p as ConvertJobModel), _ => !IsRunning);
        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => !IsRunning && Jobs.Any(j => j.State == JobState.Pending) && HasFfmpeg);
        Log($"FFmpeg: {_ffmpeg.FfmpegPath ?? "NICHT GEFUNDEN"}", "SYS");
    }

    public ObservableCollection<ConvertJobModel> Jobs { get; } = [];
    public string[] Formats { get; } = ["mp4", "mkv", "avi", "mov", "webm", "ogg"];
    public string[] Encoders { get; } = ["CPU (libx264)", "GPU (NVIDIA NVENC)", "Reparatur (Corrector)"];
    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand AddFilesCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public string Format { get => _format; set => SetProperty(ref _format, value); }
    public string Encoder { get => _encoder; set { if (SetProperty(ref _encoder, value)) OnPropertyChanged(nameof(IsRepairMode)); } }
    public bool IsRepairMode => Encoder.StartsWith("Reparatur", StringComparison.Ordinal);
    public bool OnlyBroken { get => _onlyBroken; set => SetProperty(ref _onlyBroken, value); }
    public bool UseCustomFolder { get => _useCustomFolder; set => SetProperty(ref _useCustomFolder, value); }
    public string OutputFolder { get => _outputFolder ?? "Kein Ordner gewählt"; set => SetProperty(ref _outputFolder, value); }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) RaiseCommands(); } }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }
    public bool HasFfmpeg => _ffmpeg.IsAvailable;

    public void RefreshTools() { _ffmpeg.Refresh(); OnPropertyChanged(nameof(HasFfmpeg)); RaiseCommands(); }

    private async Task AddFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Videodateien wählen", Multiselect = true,
            Filter = "Videodateien|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.vob;*.ogg|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var path in dialog.FileNames)
        {
            var job = new ConvertJobModel { Number = Jobs.Count + 1, FilePath = path };
            Jobs.Add(job);
            _ = ProbeAsync(job);
        }
        RaiseCommands();
        await Task.CompletedTask;
    }

    private async Task ProbeAsync(ConvertJobModel job) => job.Info = await _ffmpeg.ProbeAsync(job.FilePath);

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Zielordner wählen" };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    private void Remove(ConvertJobModel? job)
    {
        if (job is not null) Jobs.Remove(job);
        RaiseCommands();
    }

    private async Task StartAsync()
    {
        IsRunning = true;
        try
        {
            foreach (var job in Jobs.Where(j => j.State == JobState.Pending).ToList())
            {
                var info = job.Info ?? await _ffmpeg.ProbeAsync(job.FilePath);
                job.Info = info;
                if (IsRepairMode && OnlyBroken && !info.NeedsFix)
                {
                    job.State = JobState.Skipped;
                    Log($"Übersprungen (bereits kompatibel): {job.FileName}", "SKIP");
                    continue;
                }
                job.State = JobState.Running;
                var outputDirectory = UseCustomFolder && _outputFolder is not null ? _outputFolder : Path.GetDirectoryName(job.FilePath)!;
                Directory.CreateDirectory(outputDirectory);
                var stem = Path.GetFileNameWithoutExtension(job.FilePath);
                string output;
                var args = new List<string> { "-y", "-i", job.FilePath };
                if (IsRepairMode)
                {
                    output = Formatters.UniquePath(Path.Combine(outputDirectory, stem + "_fixed.mp4"));
                    args.AddRange(["-c:v", "libx264", "-crf", "23", "-preset", "medium", "-profile:v", "high", "-level", "4.1", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"]);
                }
                else
                {
                    output = Formatters.UniquePath(Path.Combine(outputDirectory, $"{stem}_converted.{Format}"));
                    if (Encoder.StartsWith("GPU", StringComparison.Ordinal)) args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4"]);
                    else if (Format is "mp4" or "mkv" or "mov") args.AddRange(["-c:v", "libx264", "-preset", "medium"]);
                }
                args.Add(output);
                Log("→ " + Path.GetFileName(output), "CONVERT");
                try
                {
                    var code = await _ffmpeg.RunAsync(args, info.Duration);
                    job.State = code == 0 && File.Exists(output) ? JobState.Done : JobState.Error;
                    Log(job.State == JobState.Done ? "✓ " + Path.GetFileName(output) : $"Fehler bei {job.FileName} (Code {code})", job.State == JobState.Done ? "OK" : "ERR");
                }
                catch (Exception ex) { job.State = JobState.Error; Log(ex.Message, "ERR"); }
            }
            Log("Alle Aufgaben abgeschlossen.", "FINISH");
        }
        finally { IsRunning = false; }
    }

    private void Log(string message, string tag) => LogText += $"[{tag}] {message}{Environment.NewLine}";
    private void RaiseCommands() { AddFilesCommand.RaiseCanExecuteChanged(); StartCommand.RaiseCanExecuteChanged(); RemoveCommand.RaiseCanExecuteChanged(); }
}
