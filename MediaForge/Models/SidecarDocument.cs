using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaForge.Models;

/// <summary>
/// Serialisierungsform der Sidecar-Datei „&lt;video&gt;.mediaforge.json“.
/// Unbekannte Felder werden über <see cref="Extensions"/> erhalten, damit neuere
/// Schemaversionen beim Speichern nichts verlieren.
/// </summary>
public sealed class SidecarDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string MediaId { get; set; } = string.Empty;
    public SidecarFile File { get; set; } = new();
    public SidecarMetadata Metadata { get; set; } = new();
    public List<SidecarMarker> Markers { get; set; } = [];
    public List<SidecarMarker> Ranges { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class SidecarFile
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Type { get; set; } = "video";
    public long DurationMs { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class SidecarMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class SidecarMarker
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "point";
    public long? TimestampMs { get; set; }
    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string Category { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
}
