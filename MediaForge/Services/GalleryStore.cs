using System.Text.Json;
using System.Text.Json.Serialization;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>Serialisierungsform der Bibliotheksdatei „gallery\library.json“.</summary>
public sealed class GalleryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Roots { get; set; } = [];
    public List<GalleryEntryRecord> Entries { get; set; } = [];
    public List<GalleryAlbumRecord> Albums { get; set; } = [];

    /// <summary>Angelegte Tags — auch solche, die gerade an keinem Eintrag hängen.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Gemerkte Sondierungsergebnisse, damit ein erneuter Scan nicht jedes Video neu befragen muss.</summary>
    public List<GalleryProbeRecord> Probes { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Nur Einträge, an denen wirklich etwas hängt — alles andere ergibt sich aus dem Ordner.</summary>
public sealed class GalleryEntryRecord
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Dateiname und Größe — damit eine verschobene Datei wiedererkannt wird.</summary>
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GalleryAlbumRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Paths { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// Was ffprobe über ein Video weiß, gültig solange sich das Änderungsdatum der Datei nicht bewegt.
/// Neben der Dauer stehen hier die Angaben, aus denen die Prüfung ihre Hinweise ableitet.
/// </summary>
public sealed class GalleryProbeRecord
{
    public string Path { get; set; } = string.Empty;
    public long ModifiedTicks { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Bildcodec („h264“, „vp9“ …). Leer heißt: noch nicht geprüft.</summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>Konnte ffprobe die Datei überhaupt lesen?</summary>
    public bool Ok { get; set; } = true;

    /// <summary>Enthält die Datei eine Bildspur?</summary>
    public bool HasVideo { get; set; } = true;
}

/// <summary>
/// Liest und schreibt die Bibliotheksdatei. Sie enthält nur Ordnung — Favoriten, Tags, Alben —
/// und niemals Mediendateien; geht sie verloren, bleiben alle Dateien unberührt.
/// </summary>
public static class GalleryStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Wird dies gesetzt, liegt die Bibliothek dort statt neben den Einstellungen.
    /// Gedacht für den Prüfbetrieb, damit Tests niemals die echte Sammlung anfassen.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    public static string Directory =>
        DirectoryOverride ?? Path.Combine(Path.GetDirectoryName(SettingsService.FilePath)!, "gallery");
    public static string FilePath => Path.Combine(Directory, "library.json");
    public static string ThumbnailDirectory => Path.Combine(Directory, "thumbs");

    public static GalleryDocument Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var document = JsonSerializer.Deserialize<GalleryDocument>(File.ReadAllText(FilePath), ReadOptions);
                if (document is not null) return document;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Beschädigte Bibliothek: mit leerem Stand weitermachen, statt den Start zu verhindern.
        }
        return new GalleryDocument();
    }

    /// <summary>Schreibt die Datei atomar — erst daneben, dann ersetzen.</summary>
    public static bool Save(GalleryDocument document)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, WriteOptions));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Baut aus den gemerkten Einträgen die Zuordnung Pfad → Ordnung.</summary>
    public static Dictionary<string, GalleryEntryRecord> EntryMap(GalleryDocument document)
    {
        var map = new Dictionary<string, GalleryEntryRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries)
            if (!string.IsNullOrWhiteSpace(entry.Path)) map[entry.Path] = entry;
        return map;
    }

    public static Dictionary<string, GalleryProbeRecord> ProbeMap(GalleryDocument document)
    {
        var map = new Dictionary<string, GalleryProbeRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in document.Probes)
            if (!string.IsNullOrWhiteSpace(probe.Path)) map[probe.Path] = probe;
        return map;
    }

    public static List<GalleryAlbum> ToAlbums(GalleryDocument document)
    {
        var albums = new List<GalleryAlbum>();
        foreach (var record in document.Albums)
        {
            var album = new GalleryAlbum
            {
                Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
                Name = string.IsNullOrWhiteSpace(record.Name) ? "Album" : record.Name,
                CreatedUtc = record.CreatedUtc
            };
            foreach (var path in record.Paths) album.Keys.Add(path);
            album.RefreshCount();
            albums.Add(album);
        }
        return albums;
    }

    public static List<GalleryAlbumRecord> FromAlbums(IEnumerable<GalleryAlbum> albums) =>
        [.. albums.Select(album => new GalleryAlbumRecord
        {
            Id = album.Id,
            Name = album.Name,
            CreatedUtc = album.CreatedUtc,
            Paths = [.. album.Keys.OrderBy(key => key, StringComparer.Ordinal)]
        })];
}
