using System.Collections.ObjectModel;
using MediaForge.Core;

namespace MediaForge.Models;

/// <summary>Laufzeitmodell einer Sidecar-Datei: Kopfdaten plus alle Marker eines Videos.</summary>
public sealed class MediaSidecar : ObservableObject
{
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _tagsText = string.Empty;

    public required string MediaPath { get; init; }
    public required string SidecarPath { get; init; }
    public string MediaId { get; set; } = Guid.NewGuid().ToString();
    public long DurationMs { get; set; }
    public string MediaType { get; set; } = "video";

    /// <summary>Schemaversion der geladenen Datei; höhere Werte werden beim Speichern beibehalten.</summary>
    public int SchemaVersion { get; set; } = SidecarSchema.Version;

    /// <summary>Existierte die Datei bereits beim Laden?</summary>
    public bool Existed { get; init; }

    /// <summary>Unbekannte Felder aus einer fremden/neueren Datei, die erhalten bleiben.</summary>
    public SidecarDocument? Source { get; init; }

    public ObservableCollection<MediaMarker> Markers { get; } = [];

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    /// <summary>Datei-Tags als eine Zeile, z. B. „#bundesliga #vfb“.</summary>
    public string TagsText { get => _tagsText; set => SetProperty(ref _tagsText, value); }

}

public static class SidecarSchema
{
    public const int Version = 1;
    public const string Suffix = ".mediaforge.json";
}
