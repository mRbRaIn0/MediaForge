using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public sealed partial class DownloadViewModel
{
    private async Task StartQueueAsync()
    {
        var pending = Jobs.Where(j => j.State == JobState.Pending).ToList();
        if (pending.Count == 0) { Log("Keine offenen Jobs.", "SYS"); return; }
        IsRunning = true;
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var result = await RunSingleJobAsync(pending[i]);
                if (i < pending.Count - 1 && result != JobState.Skipped)
                {
                    var wait = Random.Shared.Next(5, 16);
                    Log($"Warte {wait} Sekunden (Schutz vor Ban)…", "WAIT");
                    await Task.Delay(TimeSpan.FromSeconds(wait));
                }
            }
            Log("Alle Aufgaben erledigt.", "FINISH");
        }
        finally { IsRunning = false; _currentJob = null; }
    }

    private async Task<JobState> RunSingleJobAsync(DownloadJob job)
    {
        _currentJob = job; _skipRequested = false;
        _currentProcessCts = new CancellationTokenSource();
        job.State = JobState.Running; job.CanSkip = true; job.Progress = 0;
        RaiseCommands();
        var outputDirectory = GetOutputDirectory();
        try { Directory.CreateDirectory(outputDirectory); }
        catch (Exception ex)
        {
            job.State = JobState.Error; job.Detail = "Zielordner nicht verfügbar"; job.CanSkip = false;
            Log(ex.Message, "ERR"); AddFailed(job); _currentProcessCts.Dispose(); _currentProcessCts = null; _currentJob = null; RaiseCommands();
            return job.State;
        }

        try
        {
            var metadata = await EnsureMetadataAsync(job.Url, CookieSelection, includeThumbnail: true);
            if (metadata.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title)) job.Title = title;
            if (metadata.TryGetValue("thumbnail", out var thumbnail) && !string.IsNullOrWhiteSpace(thumbnail)) job.ThumbnailUrl = thumbnail;
            if (OutputAlreadyExists(outputDirectory, metadata))
            {
                job.State = JobState.Skipped; job.Detail = "Bereits im Zielordner vorhanden"; job.Progress = 1;
                AddFailed(job); Log("Skip vorhanden: " + (title ?? metadata.GetValueOrDefault("id") ?? job.Title), "SKIP");
                return job.State;
            }

            var startedAt = DateTime.UtcNow;
            Log($"Start: {job.Url} [{job.Quality}/{job.Format}/{job.Ratio}/{job.Encoder}] → {outputDirectory}", "DL");
            var sections = job.Section.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => SectionRegex().IsMatch(value)).ToList();
            bool ok;
            string? resultFile;
            if (sections.Count > 1)
            {
                (ok, resultFile) = await DownloadMultipleSectionsAsync(job, sections, outputDirectory);
            }
            else
            {
                var template = Path.Combine(outputDirectory, "%(title)s_%(id)s.%(ext)s");
                var args = BuildBaseArguments(job, template, CookieSelection);
                if (sections.Count == 1) AddSectionArguments(args, sections[0]);
                args.Add(job.Url);
                var code = await RunYtDlpAsync(job, args);
                if (_skipRequested) return FinishSkipped(job);
                ok = code == 0;
                resultFile = ok ? FindRecentOutput(outputDirectory, startedAt) : null;
            }

            if (_skipRequested) return FinishSkipped(job);

            if (ok)
            {
                var h264 = await EnsureMp4H264Async(job, resultFile);
                if (_skipRequested) return FinishSkipped(job);
                ok = h264;
            }
            if (ok)
            {
                var upscale = await UpscaleIfRequestedAsync(job, resultFile);
                if (_skipRequested) return FinishSkipped(job);
                ok = upscale;
            }

            if (ok)
            {
                job.State = JobState.Done; job.Progress = 1;
                job.Detail = "DL | 100% | Fertig"
                    + (job.Upscaler == "Aus" ? string.Empty : " + AI Upscale")
                    + (job.EmbedThumbnail ? " + Thumbnail" : string.Empty);
            }
            else
            {
                job.State = JobState.Error; AddFailed(job);
            }
        }
        catch (OperationCanceledException) when (_skipRequested) { return FinishSkipped(job); }
        catch (Exception ex) { job.State = JobState.Error; Log(ex.Message, "CRIT"); AddFailed(job); }
        finally
        {
            job.CanSkip = false; _currentProcessCts?.Dispose(); _currentProcessCts = null; _currentJob = null; RaiseCommands();
        }
        return job.State;
    }

    private JobState FinishSkipped(DownloadJob job)
    {
        job.State = JobState.Skipped; job.Detail = "DL | Übersprungen"; job.CanSkip = false; AddFailed(job); return job.State;
    }

    private async Task<int> RunYtDlpAsync(DownloadJob job, IReadOnlyList<string> args)
    {
        var progress = new Progress<YtDlpProgress>(update =>
        {
            job.Detail = update.Detail;
            if (update.Fraction is { } fraction) job.Progress = fraction;
        });
        return await _ytDlp.RunAsync(args, line => Log(line, "CLI"), progress,
            _currentProcessCts?.Token ?? default);
    }

    private List<string> BuildBaseArguments(DownloadJob job, string outputTemplate, string cookieSelection)
    {
        var args = new List<string> { "-o", outputTemplate };
        args.AddRange(BuildCookieArgs(cookieSelection));
        if (job.EmbedThumbnail) args.Add("--embed-thumbnail");
        if (job.Mode == "mp3")
        {
            args.AddRange(["--extract-audio", "--audio-format", job.Format.ToLowerInvariant()]);
            args.AddRange(["--audio-quality", job.Quality switch { "High" => "2", "Mid" => "5", "Low" => "9", _ => "0" }]);
            return args;
        }
        var extension = job.Format.ToLowerInvariant();
        var height = job.Quality switch { "4K" => 2160, "1080p" => 1080, "720p" => 720, "480p" => 480, _ => 0 };
        var heightFilter = height > 0 ? $"[height<={height}]" : string.Empty;
        if (extension == "mp4")
        {
            if (job.PreferH264)
            {
                const string avc = "[vcodec~='^(avc|h264)']";
                var video = $"bv*{heightFilter}[ext=mp4]{avc}/bv*{heightFilter}{avc}/bv*{heightFilter}[ext=mp4]/bv*{heightFilter}";
                var combined = $"b{heightFilter}[ext=mp4]{avc}/b{heightFilter}[ext=mp4]/b{heightFilter}";
                args.AddRange(["-f", $"({video})+(ba[ext=m4a]/ba[acodec~='^(mp4a|aac)']/ba)/{combined}", "--merge-output-format", "mp4", "-S", "+codec:avc:m4a"]);
            }
            else
            {
                var video = $"bv*{heightFilter}";
                var combined = $"b{heightFilter}";
                args.AddRange(["-f", $"{video}+ba/{combined}", "--merge-output-format", "mp4"]);
            }
        }
        else args.AddRange(["-f", $"bv*{heightFilter}[ext={extension}]+ba/b{heightFilter}[ext={extension}]/best[ext={extension}]/best"]);
        if (job.Ratio == "9:16 (Vertical)")
        {
            var post = job.Encoder == "GPU (NVIDIA)"
                ? "ffmpeg:-vf crop=trunc(ih*9/16/2)*2:ih:(iw-ow)/2:0 -c:v h264_nvenc -preset p4 -c:a aac -movflags +faststart"
                : "ffmpeg:-vf crop=trunc(ih*9/16/2)*2:ih:(iw-ow)/2:0 -c:v libx264 -preset veryfast -crf 23 -c:a aac -movflags +faststart";
            args.AddRange(["--postprocessor-args", post]);
        }
        return args;
    }

    private static void AddSectionArguments(List<string> args, string section)
    {
        args.AddRange(["--download-sections", "*" + section]);
    }

    private async Task<(bool Ok, string? File)> DownloadMultipleSectionsAsync(DownloadJob job, IReadOnlyList<string> sections, string outputDirectory)
    {
        Log($"{sections.Count} Zeitabschnitte erkannt – starte Multi-Download…", "DL");
        var prefix = "_part_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var files = new List<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            var part = $"{prefix}_{i + 1:000}";
            var template = Path.Combine(outputDirectory, $"%(title)s_%(id)s{part}.%(ext)s");
            var args = BuildBaseArguments(job, template, CookieSelection);
            AddSectionArguments(args, sections[i]); args.Add(job.Url);
            Log($"Teil {i + 1}/{sections.Count}: {sections[i]}", "DL");
            var code = await RunYtDlpAsync(job, args);
            if (_skipRequested || code != 0) return (false, null);
            var found = Directory.EnumerateFiles(outputDirectory).FirstOrDefault(f => Path.GetFileName(f).Contains(part, StringComparison.Ordinal) && !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            if (found is null) { Log($"Teil {i + 1}: Datei nicht gefunden!", "WARN"); return (false, null); }
            files.Add(found); Log($"Teil {i + 1}: {Path.GetFileName(found)}", "DL");
        }
        if (files.Count < 2) return (files.Count == 1, files.FirstOrDefault());
        var finalName = Path.GetFileName(files[0]).Replace(prefix + "_001", "_Final", StringComparison.Ordinal);
        var finalPath = Formatters.UniquePath(Path.Combine(outputDirectory, finalName));
        var merged = await MergeAsync(files, finalPath, outputDirectory);
        return (merged, merged ? finalPath : null);
    }

    private async Task<bool> MergeAsync(IReadOnlyList<string> files, string output, string directory)
    {
        var list = Path.Combine(directory, $"_concat_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.txt");
        try
        {
            await File.WriteAllLinesAsync(list, files.Select(p => $"file '{p.Replace('\\', '/')}'"));
            Log($"Merge: {files.Count} Teile → {Path.GetFileName(output)}", "MERGE");
            var code = await _ffmpeg.RunAsync(["-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", output], token: _currentProcessCts?.Token ?? default);
            if (code != 0) return false;
            foreach (var file in files) try { File.Delete(file); } catch { }
            Log("Merge erfolgreich: " + Path.GetFileName(output), "MERGE");
            return true;
        }
        finally { try { File.Delete(list); } catch { } }
    }

    private bool OutputAlreadyExists(string directory, IReadOnlyDictionary<string, string?> metadata)
    {
        var id = NormalizeName(metadata.GetValueOrDefault("id"));
        if (id.Length == 0 || !Directory.Exists(directory)) return false;
        return Directory.EnumerateFiles(directory).Where(p => !new[] { ".part", ".ytdl", ".tmp" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Select(p => NormalizeName(Path.GetFileName(p))).Any(name => name.EndsWith('_' + id, StringComparison.Ordinal) || name.Contains('_' + id + '_', StringComparison.Ordinal));
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var stem = Path.GetFileNameWithoutExtension(value);
        return Regex.Replace(InvalidNameRegex().Replace(stem, " "), @"\s+", " ").Trim(' ', '.', '_').ToLowerInvariant();
    }

    private static string? FindRecentOutput(string directory, DateTime startedAt)
    {
        return Directory.EnumerateFiles(directory)
            .Where(path => !Path.GetFileName(path).StartsWith("_concat_", StringComparison.Ordinal) && !new[] { ".part", ".ytdl", ".tmp", ".txt" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .Select(path => new FileInfo(path)).Where(file => file.LastWriteTimeUtc >= startedAt.AddSeconds(-2))
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    private async Task<bool> EnsureMp4H264Async(DownloadJob job, string? file)
    {
        if (job.Mode == "mp3" || !job.Format.Equals("MP4", StringComparison.OrdinalIgnoreCase) || file is null || !File.Exists(file) || !Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return true;
        if (!job.PreferH264 && !job.ConvertToH264) return true;
        var info = await _ffmpeg.ProbeAsync(file, _currentProcessCts?.Token ?? default);
        if (info.VideoCodec is "h264" or "avc1") return true;
        if (string.IsNullOrWhiteSpace(info.VideoCodec) && !job.Url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)) return true;
        var temp = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "_h264_tmp.mp4");
        var video = job.Encoder == "GPU (NVIDIA)" ? new[] { "-c:v", "h264_nvenc", "-preset", "p4" } : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23" };
        var args = new List<string> { "-y", "-i", file, "-map", "0:v:0", "-map", "0:a?" };
        args.AddRange(video); args.AddRange(["-c:a", "aac", "-movflags", "+faststart", temp]);
        job.Detail = "CONV | Konvertiere MP4 zu H.264…"; Log($"Konvertiere MP4 zu H.264: {Path.GetFileName(file)} ({info.VideoCodec})", "CONV");
        var progress = new Progress<double>(value =>
        {
            job.Progress = value;
            job.Detail = $"CONV | MP4 zu H.264 | {value:P0}";
        });
        var code = await _ffmpeg.RunAsync(args, info.Duration, progress, token: _currentProcessCts?.Token ?? default);
        if (_skipRequested) return false;
        if (code != 0 || !File.Exists(temp)) { try { File.Delete(temp); } catch { } return false; }
        File.Move(temp, file, true); job.Detail = "DL | 100% | Fertig + H.264"; return true;
    }

    private async Task<bool> UpscaleIfRequestedAsync(DownloadJob job, string? input)
    {
        var scale = job.Upscaler switch { "Real-ESRGAN 2x" => 2, "Real-ESRGAN 4x" => 4, _ => 0 };
        if (job.Mode == "mp3" || scale == 0) return true;
        if (input is null || !File.Exists(input)) { Log("AI Upscaler: Quelldatei nicht gefunden.", "WARN"); return true; }
        var (exe, cwd) = ResolveRealEsrgan();
        if (exe is null) { Log("AI Upscaler fehlt: " + cwd, "ERR"); return false; }
        var info = await _ffmpeg.ProbeAsync(input);
        if (info.Duration >= 600)
        {
            var choice = MessageBox.Show($"AI Upscaling kann sehr lange dauern und temporär viele Einzelbilder erzeugen.\n\nDas Video ist ca. {info.Duration / 60:0.0} Minuten lang.\n\nTrotzdem starten?", "AI Upscaling Warnung", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) { job.Detail = "DL | Upscaling übersprungen"; return true; }
        }
        job.State = JobState.Upscaling; job.Progress = 0;
        var root = Path.Combine(Path.GetDirectoryName(input)!, "_upscale_tmp_" + Guid.NewGuid().ToString("N"));
        var frames = Path.Combine(root, "frames"); var upscaled = Path.Combine(root, "upscaled");
        Directory.CreateDirectory(frames); Directory.CreateDirectory(upscaled);
        var output = Formatters.UniquePath(Path.Combine(Path.GetDirectoryName(input)!, $"{Path.GetFileNameWithoutExtension(input)}_upscaled_{scale}x.mp4"));
        var fps = Math.Max(1, info.Fps > 0 ? info.Fps : 30);
        var video = job.Encoder == "GPU (NVIDIA)" ? new[] { "-c:v", "h264_nvenc", "-preset", "p4" } : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20" };
        try
        {
            job.Detail = $"AI | Extrahiere Frames ({scale}x)…";
            if (await _ffmpeg.RunAsync(["-y", "-i", input, "-vsync", "0", Path.Combine(frames, "frame_%08d.png")], token: _currentProcessCts?.Token ?? default) != 0) return false;
            job.Detail = $"AI | Real-ESRGAN {scale}x…";
            var code = await RunLoggedAsync(exe, ["-i", frames, "-o", upscaled, "-n", $"realesr-animevideov3-x{scale}", "-s", scale.ToString(CultureInfo.InvariantCulture), "-f", "png"], "UPSCALE", cwd);
            if (code != 0) return false;
            job.Detail = "AI | Baue MP4 mit Audio…";
            var args = new List<string> { "-y", "-framerate", fps.ToString("0.######", CultureInfo.InvariantCulture), "-i", Path.Combine(upscaled, "frame_%08d.png"), "-i", input, "-map", "0:v:0", "-map", "1:a?" };
            args.AddRange(video); args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "copy", "-shortest", output]);
            code = await _ffmpeg.RunAsync(args, token: _currentProcessCts?.Token ?? default);
            if (code != 0)
            {
                try { File.Delete(output); } catch { }
                args = ["-y", "-framerate", fps.ToString("0.######", CultureInfo.InvariantCulture), "-i", Path.Combine(upscaled, "frame_%08d.png"), "-i", input, "-map", "0:v:0", "-map", "1:a?"];
                args.AddRange(video); args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", output]);
                code = await _ffmpeg.RunAsync(args, token: _currentProcessCts?.Token ?? default);
            }
            if (code != 0) return false;
            if (!job.KeepOriginal) try { File.Delete(input); } catch (Exception ex) { Log("Original konnte nicht gelöscht werden: " + ex.Message, "WARN"); }
            job.Detail = "AI | Fertig: " + Path.GetFileName(output); Log("AI Upscaling fertig: " + Path.GetFileName(output), "UPSCALE"); return true;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private async Task<int> RunLoggedAsync(string executable, IEnumerable<string> args, string tag, string? cwd = null)
    {
        async Task Line(string line) { Log(line, tag); await Task.CompletedTask; }
        var result = await ProcessRunner.RunStreamingAsync(executable, args, Line, Line, cwd, _currentProcessCts?.Token ?? default);
        return result.ExitCode;
    }

    private static (string? Exe, string Directory) ResolveRealEsrgan()
    {
        foreach (var baseDirectory in new[] { ToolLocator.ApplicationDirectory, ToolLocator.LegacyDirectory })
        {
            var directory = Path.Combine(baseDirectory, "Weiteres", "realesrgan-ncnn-vulkan-20220424-windows");
            var exe = Path.Combine(directory, "realesrgan-ncnn-vulkan.exe");
            if (File.Exists(exe) && Directory.Exists(Path.Combine(directory, "models"))) return (exe, directory);
        }
        return (null, Path.Combine(ToolLocator.ApplicationDirectory, "Weiteres", "realesrgan-ncnn-vulkan-20220424-windows"));
    }

    [GeneratedRegex("[<>:\"/\\\\|?*]+")]
    private static partial Regex InvalidNameRegex();
}
