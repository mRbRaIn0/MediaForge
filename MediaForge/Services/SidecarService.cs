using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaForge.Core;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>
/// Liest und schreibt die JSON-Sidecar-Datei neben einer Mediendatei
/// (<c>video.mp4</c> → <c>video.mediaforge.json</c>). Die Originaldatei bleibt unverändert.
/// </summary>
public static class SidecarService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new UtcTimestampConverter() }
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new UtcTimestampConverter() }
    };

    /// <summary>Schreibt Zeitstempel als schlankes ISO-8601 in UTC (2026-08-31T15:43:00Z).</summary>
    private sealed class UtcTimestampConverter : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TryGetDateTimeOffset(out var value) ? value : DateTimeOffset.UtcNow;

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Pfad der Sidecar-Datei zu einer Mediendatei.</summary>
    public static string PathFor(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + SidecarSchema.Suffix);
    }

    /// <summary>Lädt die Sidecar-Datei oder legt ein leeres Modell an, wenn noch keine existiert.</summary>
    public static async Task<MediaSidecar> LoadAsync(string mediaPath, MediaInfo? info, CancellationToken token = default)
    {
        var sidecarPath = PathFor(mediaPath);
        SidecarDocument? document = null;
        if (File.Exists(sidecarPath))
        {
            try
            {
                var text = await File.ReadAllTextAsync(sidecarPath, token);
                document = JsonSerializer.Deserialize<SidecarDocument>(text, ReadOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                document = null;
            }
        }

        var durationMs = info is { Duration: > 0 } ? Formatters.ToMs(info.Duration) : document?.File.DurationMs ?? 0;
        var sidecar = new MediaSidecar
        {
            MediaPath = mediaPath,
            SidecarPath = sidecarPath,
            Existed = document is not null,
            Source = document,
            DurationMs = durationMs,
            MediaType = info is null ? document?.File.Type ?? "video" : info.HasVideo ? "video" : "audio",
            MediaId = string.IsNullOrWhiteSpace(document?.MediaId) ? Guid.NewGuid().ToString() : document!.MediaId,
            SchemaVersion = Math.Max(SidecarSchema.Version, document?.SchemaVersion ?? SidecarSchema.Version)
        };

        if (document is not null)
        {
            sidecar.Title = document.Metadata.Title;
            sidecar.Description = document.Metadata.Description;
            sidecar.TagsText = FormatTags(document.Metadata.Tags);
            foreach (var entry in document.Markers.Concat(document.Ranges))
                sidecar.Markers.Add(FromDocument(entry));
        }
        else
        {
            sidecar.Title = Path.GetFileNameWithoutExtension(mediaPath);
        }

        Sort(sidecar.Markers);
        return sidecar;
    }

    /// <summary>Schreibt die Sidecar-Datei atomar (erst temporär, dann ersetzen).</summary>
    public static async Task SaveAsync(MediaSidecar sidecar, CancellationToken token = default)
    {
        var document = ToDocument(sidecar);
        var json = JsonSerializer.Serialize(document, WriteOptions);
        var directory = Path.GetDirectoryName(sidecar.SidecarPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = sidecar.SidecarPath + ".tmp";
        await File.WriteAllTextAsync(temporary, json, token);
        File.Move(temporary, sidecar.SidecarPath, overwrite: true);
    }

    /// <summary>
    /// Macht aus „#Tor, Müller  highlight“ die Liste „tor“, „müller“, „highlight“ —
    /// klein geschrieben, ohne Rauten und ohne Dubletten.
    /// </summary>
    public static List<string> NormalizeTags(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        foreach (var raw in input.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
            if (tag.Length == 0) continue;
            if (!result.Contains(tag, StringComparer.Ordinal)) result.Add(tag);
        }
        return result;
    }

    public static string FormatTags(IEnumerable<string>? tags) =>
        tags is null ? string.Empty : string.Join(' ', tags.Select(t => '#' + t));

    /// <summary>Sortiert Marker nach Startzeit, Punkte vor Bereichen.</summary>
    public static void Sort(System.Collections.ObjectModel.ObservableCollection<MediaMarker> markers)
    {
        var ordered = markers.OrderBy(m => m.StartMs).ThenBy(m => m.IsRange ? 1 : 0).ThenBy(m => m.EndMs).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var index = markers.IndexOf(ordered[i]);
            if (index != i) markers.Move(index, i);
        }
    }

    private static MediaMarker FromDocument(SidecarMarker entry)
    {
        var isRange = string.Equals(entry.Type, "range", StringComparison.OrdinalIgnoreCase) ||
                      (entry.TimestampMs is null && entry.EndMs is not null);
        var start = entry.StartMs ?? entry.TimestampMs ?? 0;
        var end = entry.EndMs ?? start;
        return new MediaMarker
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? MediaMarker.NewId(isRange ? MarkerKind.Range : MarkerKind.Point) : entry.Id,
            Kind = isRange ? MarkerKind.Range : MarkerKind.Point,
            StartMs = Math.Max(0, start),
            EndMs = Math.Max(start, end),
            Title = entry.Title ?? string.Empty,
            Note = entry.Note ?? string.Empty,
            Category = entry.Category ?? string.Empty,
            Tags = NormalizeTags(string.Join(' ', entry.Tags ?? [])),
            CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = entry.UpdatedAt ?? entry.CreatedAt ?? DateTimeOffset.UtcNow
        };
    }

    private static SidecarDocument ToDocument(MediaSidecar sidecar)
    {
        var previous = sidecar.Source;
        var document = new SidecarDocument
        {
            SchemaVersion = Math.Max(SidecarSchema.Version, sidecar.SchemaVersion),
            MediaId = sidecar.MediaId,
            Extensions = previous?.Extensions,
            File = new SidecarFile
            {
                Name = Path.GetFileName(sidecar.MediaPath),
                RelativePath = "./" + Path.GetFileName(sidecar.MediaPath),
                Type = sidecar.MediaType,
                DurationMs = sidecar.DurationMs,
                Extensions = previous?.File.Extensions
            },
            Metadata = new SidecarMetadata
            {
                Title = sidecar.Title,
                Description = sidecar.Description,
                Tags = NormalizeTags(sidecar.TagsText),
                Extensions = previous?.Metadata.Extensions
            }
        };

        var known = previous is null
            ? new Dictionary<string, SidecarMarker>(StringComparer.Ordinal)
            : previous.Markers.Concat(previous.Ranges).Where(m => !string.IsNullOrEmpty(m.Id))
                      .GroupBy(m => m.Id, StringComparer.Ordinal)
                      .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var marker in sidecar.Markers.OrderBy(m => m.StartMs))
        {
            known.TryGetValue(marker.Id, out var original);
            var entry = new SidecarMarker
            {
                Id = marker.Id,
                Type = marker.IsRange ? "range" : "point",
                Title = marker.Title,
                Note = marker.Note,
                Tags = [.. marker.Tags],
                Category = marker.Category,
                CreatedAt = marker.CreatedAt,
                UpdatedAt = marker.UpdatedAt,
                Extensions = original?.Extensions
            };
            if (marker.IsRange)
            {
                entry.StartMs = marker.StartMs;
                entry.EndMs = Math.Max(marker.StartMs, marker.EndMs);
                document.Ranges.Add(entry);
            }
            else
            {
                entry.TimestampMs = marker.StartMs;
                document.Markers.Add(entry);
            }
        }
        return document;
    }
}
