namespace MediaForge.Models;

/// <summary>Wie eine Datei im Sorter angezeigt wird: Video mit Player, Bild als Standbild, alles andere ohne Vorschau.</summary>
public enum SorterItemKind { Video, Image, File }

/// <summary>Ein Zielbereich des Sorters — Anzeigename, Ordner und optional eine fest vergebene Taste.</summary>
public sealed class SorterTarget
{
    public string Name { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;

    /// <summary>Leer bedeutet: Taste wird automatisch nach Reihenfolge vergeben.</summary>
    public string Key { get; set; } = string.Empty;

    public SorterTarget Clone() => new() { Name = Name, Folder = Folder, Key = Key };
}

/// <summary>Eine gefundene Datei im Startordner samt der Angaben, die Anzeige und Zielpfad brauchen.</summary>
public sealed class SorterItem
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public string Subfolder { get; init; } = string.Empty;
    public long Size { get; init; }
    public SorterItemKind Kind { get; init; }

    public string Extension { get; init; } = string.Empty;
}
