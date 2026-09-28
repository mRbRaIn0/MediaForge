using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed partial class FfmpegService
{
    public string? FfmpegPath { get; private set; }
    public string? FfprobePath { get; private set; }
    public string? FfplayPath { get; private set; }
    public bool IsAvailable => FfmpegPath is not null;

    public FfmpegService() => Refresh();

    public void Refresh()
    {
        FfmpegPath = ToolLocator.Find("ffmpeg.exe", SettingsService.Current.FfmpegPath);
        var directory = FfmpegPath is null ? null : Path.GetDirectoryName(FfmpegPath);
        FfprobePath = ToolLocator.Find("ffprobe.exe", Sibling(directory, "ffprobe.exe"));
        FfplayPath = ToolLocator.Find("ffplay.exe", Sibling(directory, "ffplay.exe"));
    }

    private static string? Sibling(string? directory, string name) =>
        string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, name);

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        if (FfprobePath is null || !File.Exists(path)) return new MediaInfo { Ok = false };
        try
        {
            var result = await ProcessRunner.CaptureAsync(FfprobePath,
                ["-v", "quiet", "-print_format", "json", "-show_streams", "-show_format", path],
                cancellationToken: token);
            if (result.ExitCode != 0) return new MediaInfo { Ok = false, NeedsFix = true, VideoCodec = "Fehler" };
            using var json = JsonDocument.Parse(result.StandardOutput);
            var root = json.RootElement;
            var hasVideo = false;
            var hasAudio = false;
            var videoCodec = "-";
            var audioCodec = "-";
            var pixel = "yuv420p";
            var width = 0;
            var height = 0;
            var fps = 0d;
            if (root.TryGetProperty("streams", out var streams))
            foreach (var stream in streams.EnumerateArray())
            {
                var kind = GetString(stream, "codec_type");
                if (kind == "video" && !hasVideo)
                {
                    hasVideo = true;
                    videoCodec = GetString(stream, "codec_name", "?");
                    pixel = GetString(stream, "pix_fmt", "yuv420p");
                    width = GetInt(stream, "width");
                    height = GetInt(stream, "height");
                    var rate = GetString(stream, "r_frame_rate", GetString(stream, "avg_frame_rate", "0/1"));
                    fps = ParseRate(rate);
                }
                else if (kind == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    audioCodec = GetString(stream, "codec_name", "?");
                }
            }

            var duration = 0d;
            long? size = null;
            var formatName = string.Empty;
            if (root.TryGetProperty("format", out var format))
            {
                duration = GetDouble(format, "duration");
                formatName = GetString(format, "format_name");
                var sizeText = GetString(format, "size");
                if (long.TryParse(sizeText, out var parsedSize)) size = parsedSize;
            }
            var needsFix = hasVideo && videoCodec is not ("h264" or "avc1") ||
                           (!formatName.Contains("mp4", StringComparison.OrdinalIgnoreCase) &&
                            !formatName.Contains("mov", StringComparison.OrdinalIgnoreCase));
            return new MediaInfo
            {
                Duration = duration, Size = size, VideoCodec = videoCodec, AudioCodec = audioCodec,
                Width = width, Height = height, Fps = fps, PixelFormat = pixel, FormatName = formatName,
                HasVideo = hasVideo, HasAudio = hasAudio, NeedsFix = needsFix
            };
        }
        catch { return new MediaInfo { Ok = false, NeedsFix = true, VideoCodec = "Fehler" }; }
    }

    public async Task<byte[]?> ExtractFrameAsync(string path, double seconds, int width = 480, CancellationToken token = default)
    {
        if (FfmpegPath is null || !File.Exists(path)) return null;
        var temp = Path.Combine(Path.GetTempPath(), $"mediaforge_frame_{Guid.NewGuid():N}.png");
        try
        {
            var result = await ProcessRunner.CaptureAsync(FfmpegPath,
                ["-y", "-loglevel", "quiet", "-ss", F(seconds), "-i", path, "-frames:v", "1", "-vf", $"scale={width}:-2", temp],
                cancellationToken: token);
            return result.ExitCode == 0 && File.Exists(temp) ? await File.ReadAllBytesAsync(temp, token) : null;
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task<int> RunAsync(IEnumerable<string> arguments, double duration = 0,
        IProgress<double>? progress = null, Action<string>? log = null, CancellationToken token = default)
    {
        if (FfmpegPath is null) throw new FileNotFoundException("ffmpeg.exe wurde nicht gefunden.");
        async Task Line(string line)
        {
            log?.Invoke(line);
            if (duration > 0)
            {
                var match = ProgressRegex().Match(line);
                if (match.Success)
                {
                    var seconds = int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60 +
                                  double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    progress?.Report(Math.Clamp(seconds / duration, 0, 1));
                }
            }
            await Task.CompletedTask;
        }
        var result = await ProcessRunner.RunStreamingAsync(FfmpegPath, arguments, null, Line, cancellationToken: token);
        return result.ExitCode;
    }

    public async Task<bool> UpdateAsync(IProgress<string>? status = null, CancellationToken token = default)
    {
        const string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        var target = ResolveUpdateTarget();
        Directory.CreateDirectory(target);
        var zipPath = Path.Combine(Path.GetTempPath(), $"ffmpeg_{Guid.NewGuid():N}.zip");
        try
        {
            status?.Report("Zielordner: " + target);
            status?.Report("Lade FFmpeg-Paket (~100 MB) von BtbN/FFmpeg-Builds…");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaForge/1.0");
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(zipPath);
                await CopyWithProgressAsync(input, output, total, status, token);
            }
            status?.Report("Entpacke ffmpeg, ffplay & ffprobe…");
            using var archive = ZipFile.OpenRead(zipPath);
            var failed = new List<string>();
            foreach (var name in new[] { "ffmpeg.exe", "ffplay.exe", "ffprobe.exe" })
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith('/' + name, StringComparison.OrdinalIgnoreCase));
                if (entry is null) { failed.Add(name); status?.Report($"{name} nicht im Archiv gefunden."); continue; }
                // Einzeln abfangen: eine gesperrte Datei (laufender ffplay/ffmpeg) darf die anderen nicht verhindern.
                try
                {
                    entry.ExtractToFile(Path.Combine(target, name), overwrite: true);
                    status?.Report($"{name} entpackt ({entry.Length / 1048576d:0.0} MiB).");
                }
                catch (Exception ex)
                {
                    failed.Add(name);
                    status?.Report($"{name} konnte nicht geschrieben werden ({ex.Message}) — läuft der Prozess noch?");
                }
            }
            Refresh();
            if (failed.Count > 0) status?.Report("Nicht aktualisiert: " + string.Join(", ", failed));
            else status?.Report("FFmpeg, FFplay & FFprobe aktualisiert.");
            return failed.Count == 0 && FfmpegPath is not null && FfprobePath is not null && FfplayPath is not null;
        }
        catch (Exception ex) { status?.Report($"Update-Fehler: {ex.Message}"); return false; }
        finally { try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { } }
    }

    private static async Task CopyWithProgressAsync(Stream input, Stream output, long total,
        IProgress<string>? status, CancellationToken token)
    {
        var buffer = new byte[81920];
        long copied = 0;
        long reported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read;
            if (total > 0)
            {
                var percent = copied * 100 / total;
                if (percent < reported + 5) continue;
                reported = percent;
                status?.Report($"Download {percent,3}% | {copied / 1048576d:0.0} / {total / 1048576d:0.0} MiB");
            }
            else if (copied >= reported + 10 * 1048576L)
            {
                reported = copied;
                status?.Report($"Download {copied / 1048576d:0.0} MiB…");
            }
        }
        status?.Report($"Download abgeschlossen ({copied / 1048576d:0.0} MiB).");
    }

    /// <summary>Zielordner für das Update: konfigurierter Pfad, sonst der Ordner der aktuellen ffmpeg.exe.</summary>
    private string ResolveUpdateTarget()
    {
        var configured = SettingsService.Current.FfmpegPath.Trim().Trim('"');
        if (configured.Length > 0)
        {
            if (Directory.Exists(configured)) return configured;
            if (Path.IsPathRooted(configured))
            {
                var directory = Path.GetDirectoryName(configured);
                if (!string.IsNullOrEmpty(directory)) return directory;
            }
        }
        return FfmpegPath is null ? ToolLocator.DefaultToolDirectory : Path.GetDirectoryName(FfmpegPath)!;
    }

    public static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string GetString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) ? value.ToString() : fallback;
    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static double ParseRate(string rate)
    {
        var split = rate.Split('/');
        if (split.Length == 2 && double.TryParse(split[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(split[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0) return n / d;
        return 0;
    }

    [GeneratedRegex(@"time=(\d+):(\d+):(\d+\.?\d*)")]
    private static partial Regex ProgressRegex();
}
