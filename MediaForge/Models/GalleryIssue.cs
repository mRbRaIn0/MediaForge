using System.Collections.ObjectModel;
using MediaForge.Core;

namespace MediaForge.Models;

/// <summary>Ein Fund der Prüfung: eine Datei und der Grund, warum sie auffällt.</summary>
public sealed class GalleryIssue
{
    public required GalleryItem Item { get; init; }

    /// <summary>Kurz und im Klartext, z. B. „VP9 — der eingebaute Player zeigt kein Bild“.</summary>
    public required string Reason { get; init; }

    /// <summary>Bei Duplikaten der älteste Stand — der bleibt üblicherweise stehen.</summary>
    public bool IsOriginal { get; init; }

    /// <summary>Ein echter Fehler und keine bloße Doppelung — färbt den Grund im Bereich ein.</summary>
    public bool IsFault { get; init; }

    public string Name => Item.Name;
    public string FullPath => Item.FullPath;
    public string SizeText => Item.SizeText;

    /// <summary>Wo die Datei liegt — Wurzelordner plus Unterordner, damit Duplikate unterscheidbar sind.</summary>
    public string Location => Item.Subfolder.Length == 0
        ? System.IO.Path.GetFileName(Item.RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar))
        : System.IO.Path.Combine(
            System.IO.Path.GetFileName(Item.RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
            Item.Subfolder);
}

/// <summary>
/// Mehrere Dateien mit gleichem Inhalt. Die Liste beginnt mit dem ältesten Stand;
/// alles danach ist eine Kopie, die Platz kostet.
/// </summary>
public sealed class GalleryDuplicateGroup
{
    public ObservableCollection<GalleryIssue> Items { get; } = [];

    /// <summary>Größe einer einzelnen Datei — alle in der Gruppe sind gleich groß.</summary>
    public long Size { get; init; }

    public string Header => $"{Items.Count}× {Items.FirstOrDefault()?.Name ?? string.Empty}";

    /// <summary>Was die Kopien zusammen belegen; die erste Datei zählt nicht als Verschwendung.</summary>
    public long Wasted => Size * Math.Max(0, Items.Count - 1);

    public string WastedText => Formatters.Size(Wasted) + " doppelt";
}
