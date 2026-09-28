using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed class LinkViewModel : ObservableObject, IToolAware
{
    private readonly FfmpegService _ffmpeg;
    private string _outputName = string.Empty;
    private string? _outputFolder;
    private string _status = "Bereit";
    private string _modeText = "Noch keine Dateien hinzugefügt.";
    private bool _busy;
    private double _progress;

    public LinkViewModel(FfmpegService ffmpeg, Action back)
    {
        _ffmpeg = ffmpeg;
        BackCommand = new RelayCommand(_ => back());
        AddFilesCommand = new AsyncRelayCommand(_ => AddFilesAsync(), _ => !Busy);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !Busy);
        RemoveCommand = new RelayCommand(p => Remove(p as LinkItemModel), _ => !Busy);
        MoveUpCommand = new RelayCommand(p => Move(p as LinkItemModel, -1), _ => !Busy);
        MoveDownCommand = new RelayCommand(p => Move(p as LinkItemModel, 1), _ => !Busy);
        JoinCommand = new AsyncRelayCommand(_ => JoinAsync(), _ => !Busy && Items.Count >= 2 && HasFfmpeg);
    }

    public ObservableCollection<LinkItemModel> Items { get; } = [];
    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand AddFilesCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }
    public string OutputName { get => _outputName; set => SetProperty(ref _outputName, value); }
    public string OutputFolder { get => _outputFolder ?? "(Ordner des 1. Videos)"; set => SetProperty(ref _outputFolder, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) RaiseCommands(); } }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, Math.Clamp(value, 0, 1)); }
    public bool HasFfmpeg => _ffmpeg.IsAvailable;

    public void RefreshTools() { _ffmpeg.Refresh(); OnPropertyChanged(nameof(HasFfmpeg)); RaiseCommands(); }

    private async Task AddFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = "Videodateien wählen", Multiselect = true, Filter = "Videodateien|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.vob;*.ogg|Alle Dateien|*.*" };
        if (dialog.ShowDialog() != true) return;
        foreach (var path in dialog.FileNames)
        {
            var item = new LinkItemModel { FilePath = path };
            Items.Add(item);
            _ = ProbeAsync(item);
        }
        if (string.IsNullOrWhiteSpace(OutputName) && dialog.FileNames.Length > 0)
            OutputName = $"{Path.GetFileNameWithoutExtension(dialog.FileNames[0])}_joined{Path.GetExtension(dialog.FileNames[0])}";
        UpdateMode(); RaiseCommands();
        await Task.CompletedTask;
    }

    private async Task ProbeAsync(LinkItemModel item) { item.Info = await _ffmpeg.ProbeAsync(item.FilePath); UpdateMode(); }
    private void ChooseFolder() { var dialog = new OpenFolderDialog { Title = "Zielordner wählen" }; if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName; }
    private void Remove(LinkItemModel? item) { if (item is not null) Items.Remove(item); UpdateMode(); RaiseCommands(); }
    private void Move(LinkItemModel? item, int delta)
    {
        if (item is null) return;
        var old = Items.IndexOf(item); var target = old + delta;
        if (old >= 0 && target >= 0 && target < Items.Count) Items.Move(old, target);
    }

    private static bool Compatible(IReadOnlyList<MediaInfo> infos)
    {
        if (infos.Count < 2 || infos.Any(i => !i.HasVideo)) return false;
        var first = infos[0];
        return infos.All(i => i.VideoCodec == first.VideoCodec && i.Width == first.Width && i.Height == first.Height &&
                              i.PixelFormat == first.PixelFormat && i.AudioCodec == first.AudioCodec && i.HasAudio == first.HasAudio);
    }

    private void UpdateMode()
    {
        if (Items.Count == 0) ModeText = "Noch keine Dateien hinzugefügt.";
        else if (Items.Count < 2) ModeText = "Mindestens 2 Videos nötig.";
        else if (Items.Any(i => i.Info is null)) ModeText = "Prüfe Formate…";
        else ModeText = Compatible(Items.Select(i => i.Info!).ToList())
            ? "Modus: Schnell verbinden — ohne Encoding (gleiche Formate)."
            : "Modus: Re-Encode nötig — unterschiedliche Formate/Auflösungen.";
    }

    private async Task JoinAsync()
    {
        var first = Items[0].FilePath;
        var directory = _outputFolder ?? Path.GetDirectoryName(first)!;
        var name = string.IsNullOrWhiteSpace(OutputName) ? $"{Path.GetFileNameWithoutExtension(first)}_joined{Path.GetExtension(first)}" : OutputName;
        var output = Formatters.UniquePath(Path.Combine(directory, name));
        Busy = true; Progress = 0;
        var temp = Path.Combine(Path.GetTempPath(), "mediaforge_link_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var infos = new List<MediaInfo>();
            foreach (var item in Items) infos.Add(item.Info ?? await _ffmpeg.ProbeAsync(item.FilePath));
            var total = Math.Max(1, infos.Sum(i => i.Duration));
            var encoded = false;
            var ok = false;
            if (Compatible(infos))
            {
                Status = "Schnell verbinden (ohne Encoding)…";
                ok = await ConcatCopyAsync(Items.Select(i => i.FilePath).ToList(), output, temp, total);
            }
            if (!ok)
            {
                encoded = true; Status = "Unterschiedliche Formate — encodiere…";
                ok = await ConcatEncodeAsync(infos, output, temp, total);
            }
            if (ok && File.Exists(output)) { Progress = 1; Status = $"Fertig ({(encoded ? "encodiert" : "ohne Encoding")}): {Path.GetFileName(output)} ({Formatters.Size(new FileInfo(output).Length)})"; }
            else { Progress = 0; Status = "Verbinden fehlgeschlagen."; }
        }
        catch (Exception ex) { Status = "Fehler: " + ex.Message; }
        finally { try { Directory.Delete(temp, true); } catch { } Busy = false; }
    }

    private async Task<bool> ConcatCopyAsync(IReadOnlyList<string> paths, string output, string temp, double total)
    {
        var listPath = Path.Combine(temp, "list.txt");
        await File.WriteAllLinesAsync(listPath, paths.Select(p => $"file '{p.Replace("'", "'\\''").Replace('\\', '/')}'"));
        var args = new List<string> { "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy" };
        if (new[] { ".mp4", ".mov", ".m4v" }.Contains(Path.GetExtension(output).ToLowerInvariant())) args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);
        return await _ffmpeg.RunAsync(args, total, new Progress<double>(p => Progress = Math.Min(.99, p))) == 0;
    }

    private async Task<bool> ConcatEncodeAsync(IReadOnlyList<MediaInfo> infos, string output, string temp, double total)
    {
        var width = infos.Max(i => i.Width) is var w && w > 0 ? w - w % 2 : 1280;
        var height = infos.Max(i => i.Height) is var h && h > 0 ? h - h % 2 : 720;
        var fps = Math.Min(60, infos.Max(i => i.Fps) is var f && f > 0 ? f : 30);
        var normalized = new List<string>();
        var done = 0d;
        for (var i = 0; i < Items.Count; i++)
        {
            var seg = Path.Combine(temp, $"n{i:000}.mp4");
            var info = infos[i];
            var filter = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps.ToString("g", CultureInfo.InvariantCulture)}";
            var args = new List<string> { "-y", "-i", Items[i].FilePath };
            if (!info.HasAudio) args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
            args.AddRange(["-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"]);
            if (!info.HasAudio) args.Add("-shortest");
            args.Add(seg);
            var before = done;
            var code = await _ffmpeg.RunAsync(args, info.Duration, new Progress<double>(p => Progress = Math.Min(.98, (before + p * info.Duration) / total)));
            if (code != 0 || !File.Exists(seg)) return false;
            normalized.Add(seg); done += info.Duration;
        }
        return await ConcatCopyAsync(normalized, output, temp, total);
    }

    private void RaiseCommands() { AddFilesCommand.RaiseCanExecuteChanged(); JoinCommand.RaiseCanExecuteChanged(); RemoveCommand.RaiseCanExecuteChanged(); }
}
