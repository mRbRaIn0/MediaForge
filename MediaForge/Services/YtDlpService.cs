using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaForge.Services;

public sealed record YtDlpProgress(double? Fraction, string Detail);

public sealed partial class YtDlpService
{
    public string? ExecutablePath { get; private set; }
    public bool IsAvailable => ExecutablePath is not null;

    public YtDlpService() => Refresh();
    public void Refresh() => ExecutablePath = ToolLocator.Find("yt-dlp.exe", SettingsService.Current.YtDlpPath);

    public async Task<Dictionary<string, string?>> FetchMetadataAsync(string url, IEnumerable<string> cookieArgs,
        CancellationToken token = default)
    {
        EnsureAvailable();
        var args = new List<string>();
        args.AddRange(cookieArgs);
        args.AddRange(["--dump-single-json", "--skip-download", "--no-warnings", "--no-playlist", url]);
        var result = await ProcessRunner.CaptureAsync(ExecutablePath!, args, cancellationToken: token);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)) return [];
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var first = entries.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            if (first.ValueKind == JsonValueKind.Object) root = first;
        }
        string? S(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : null;
        var thumbnail = S("thumbnail") ?? S("thumbnail_url") ?? S("thumb") ?? S("cover") ?? S("image") ?? S("poster");
        if (thumbnail is null && root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in thumbs.EnumerateArray().Reverse())
                if (item.TryGetProperty("url", out var candidate)) { thumbnail = candidate.ToString(); break; }
        }
        return new Dictionary<string, string?>
        {
            ["title"] = S("title"), ["id"] = S("id"), ["duration"] = S("duration"), ["thumbnail"] = thumbnail
        };
    }

    public async Task<int> RunAsync(IEnumerable<string> arguments, Action<string> log,
        IProgress<YtDlpProgress>? status = null, CancellationToken token = default)
    {
        EnsureAvailable();
        async Task Handle(string raw)
        {
            var line = AnsiRegex().Replace(raw, string.Empty).Trim();
            if (line.Length == 0) return;
            var compact = SpaceRegex().Replace(line, " ");
            var progress = DownloadProgressRegex().Match(compact);
            if (progress.Success)
            {
                var value = double.Parse(progress.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                status?.Report(new YtDlpProgress(value / 100,
                    $"DL | {progress.Groups[1].Value}% | {progress.Groups[2].Value} | {progress.Groups[3].Value} | ETA {progress.Groups[4].Value}"));
            }
            else if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
                status?.Report(new YtDlpProgress(null, "DL | " + line[(line.IndexOf(':') + 1)..].Trim()));
            else if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase))
                status?.Report(new YtDlpProgress(null, "DL | Merging Formate…"));
            else if (line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase))
                status?.Report(new YtDlpProgress(null, "DL | Konvertiere Audio…"));
            else if (line.Contains("[ffmpeg]", StringComparison.OrdinalIgnoreCase))
                status?.Report(new YtDlpProgress(null, "DL | FFmpeg Verarbeitung…"));
            log(line);
            await Task.CompletedTask;
        }
        var result = await ProcessRunner.RunStreamingAsync(ExecutablePath!, arguments, Handle, Handle, cancellationToken: token);
        return result.ExitCode;
    }

    public async Task<bool> UpdateAsync(Action<string> log, CancellationToken token = default)
    {
        if (ExecutablePath is null)
        {
            var target = ResolveDownloadTarget();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaForge/1.0");
                log("yt-dlp wird heruntergeladen…");
                var bytes = await client.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", token);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, token);
                Refresh();
                log("yt-dlp wurde installiert.");
                return true;
            }
            catch (Exception ex) { log("Update-Fehler: " + ex.Message); return false; }
        }
        var result = await ProcessRunner.RunStreamingAsync(ExecutablePath, ["-U"],
            line => { log(line); return Task.CompletedTask; },
            line => { log(line); return Task.CompletedTask; }, cancellationToken: token);
        Refresh();
        return result.ExitCode == 0;
    }

    /// <summary>Ablageort für eine frische yt-dlp.exe: konfigurierter Pfad, sonst der Standard-Werkzeugordner.</summary>
    private static string ResolveDownloadTarget()
    {
        var configured = SettingsService.Current.YtDlpPath.Trim().Trim('"');
        if (configured.Length > 0)
        {
            if (Directory.Exists(configured)) return Path.Combine(configured, "yt-dlp.exe");
            if (Path.IsPathRooted(configured)) return configured;
        }
        return Path.Combine(ToolLocator.DefaultToolDirectory, "yt-dlp.exe");
    }

    private void EnsureAvailable()
    {
        if (ExecutablePath is null) throw new FileNotFoundException("yt-dlp.exe wurde nicht gefunden. Nutze zuerst ‚yt-dlp Update‘.");
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
    [GeneratedRegex(@"\[download\]\s+(\d{1,3}(?:\.\d+)?)%\s+of\s+(.+?)\s+at\s+(.+?)\s+ETA\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadProgressRegex();
}
