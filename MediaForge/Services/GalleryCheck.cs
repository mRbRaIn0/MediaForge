using System.Security.Cryptography;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>
/// Sucht in der Bibliothek nach zwei Sorten Ärger: Dateien, die mehrfach herumliegen,
/// und Dateien, die sich nicht ansehen lassen. Es wird dabei nur gelesen — nichts
/// verschoben, nichts gelöscht, nichts umbenannt.
/// </summary>
public static class GalleryCheck
{
    /// <summary>
    /// Videoformate, die der eingebaute Player (Media Foundation) ohne Zusatzpaket nicht zeigt.
    /// Der Wert ist der Name, wie er im Hinweis stehen soll.
    /// </summary>
    private static readonly Dictionary<string, string> AwkwardCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vp9"] = "VP9",
        ["vp8"] = "VP8",
        ["vp6"] = "VP6",
        ["av1"] = "AV1",
        ["theora"] = "Theora",
        ["prores"] = "ProRes",
        ["dnxhd"] = "DNxHD",
        ["flv1"] = "Sorenson Spark",
        ["msmpeg4v3"] = "MS-MPEG-4 v3"
    };

    /// <summary>HEVC läuft nur mit der HEVC-Erweiterung von Windows — deshalb ein eigener Hinweis.</summary>
    private static readonly HashSet<string> HevcCodecs = new(StringComparer.OrdinalIgnoreCase) { "hevc", "h265" };

    /// <summary>
    /// Beurteilt ein Video anhand der ffprobe-Auskunft. Ergebnis ist der Grund für den Hinweis
    /// oder <c>null</c>, wenn mit der Datei alles in Ordnung ist.
    /// </summary>
    public static string? VideoProblem(MediaInfo? info)
    {
        if (info is null) return null;
        if (!info.Ok) return "Lässt sich nicht lesen — die Datei ist vermutlich beschädigt oder abgebrochen.";
        if (!info.HasVideo) return "Enthält keine Bildspur — nur Ton oder gar nichts.";
        if (info.Duration <= 0) return "Ohne Laufzeit — die Datei ist vermutlich unvollständig.";
        if (AwkwardCodecs.TryGetValue(info.VideoCodec, out var name))
            return $"{name} statt H.264 — der eingebaute Player zeigt dafür kein Bild.";
        if (HevcCodecs.Contains(info.VideoCodec))
            return "HEVC (H.265) — spielt nur, wenn die HEVC-Erweiterung von Windows installiert ist.";
        return null;
    }

    /// <summary>
    /// Beurteilt ein Bild, indem es probeweise dekodiert wird. Ergebnis ist der Grund für den
    /// Hinweis oder <c>null</c>, wenn sich das Bild öffnen lässt.
    /// </summary>
    public static string? ImageProblem(GalleryItem item)
    {
        if (!File.Exists(item.FullPath)) return "Die Datei ist nicht mehr da.";
        if (item.Size == 0) return "Die Datei ist leer (0 Byte).";
        return GalleryService.Load(item.FullPath, 64) is null
            ? "Lässt sich nicht öffnen — die Datei ist vermutlich beschädigt."
            : null;
    }

    /// <summary>Gilt für beide Arten: eine leere oder verschwundene Datei ist immer ein Fund.</summary>
    public static string? FileProblem(GalleryItem item)
    {
        if (!File.Exists(item.FullPath)) return "Die Datei ist nicht mehr da.";
        return item.Size == 0 ? "Die Datei ist leer (0 Byte)." : null;
    }

    /// <summary>
    /// Findet Dateien mit gleichem Inhalt. Nur gleich große Dateien kommen überhaupt infrage;
    /// innerhalb dieser Gruppen entscheidet ein Fingerabdruck aus Anfang und Ende der Datei.
    /// So bleibt die Prüfung auch bei großen Videos schnell.
    /// </summary>
    public static List<GalleryDuplicateGroup> FindDuplicates(IReadOnlyList<GalleryItem> items,
        IProgress<int>? progress = null, CancellationToken token = default)
    {
        var groups = new List<GalleryDuplicateGroup>();
        var bySize = items
            .Where(item => item.Size > 0)
            .GroupBy(item => item.Size)
            .Where(group => group.Count() > 1);

        var done = 0;
        foreach (var sizeGroup in bySize)
        {
            token.ThrowIfCancellationRequested();
            var byFingerprint = new Dictionary<string, List<GalleryItem>>(StringComparer.Ordinal);
            foreach (var item in sizeGroup)
            {
                var fingerprint = Fingerprint(item.FullPath, item.Size);
                if (fingerprint is null) continue;
                if (!byFingerprint.TryGetValue(fingerprint, out var bucket))
                    byFingerprint[fingerprint] = bucket = [];
                bucket.Add(item);
                progress?.Report(++done);
            }

            foreach (var bucket in byFingerprint.Values.Where(bucket => bucket.Count > 1))
            {
                // Der älteste Stand steht oben — er ist üblicherweise das Original.
                var ordered = bucket.OrderBy(item => item.ModifiedTicks).ThenBy(item => item.FullPath).ToList();
                var group = new GalleryDuplicateGroup { Size = ordered[0].Size };
                for (var index = 0; index < ordered.Count; index++)
                {
                    group.Items.Add(new GalleryIssue
                    {
                        Item = ordered[index],
                        IsOriginal = index == 0,
                        Reason = index == 0 ? "Ältester Stand" : "Kopie"
                    });
                }
                groups.Add(group);
            }
        }

        return [.. groups.OrderByDescending(group => group.Wasted)];
    }

    /// <summary>
    /// Größe plus je 256 KB vom Anfang und vom Ende. Das trennt gleich große, aber verschiedene
    /// Dateien zuverlässig, ohne Gigabytes durch die Prüfsumme zu schicken.
    /// </summary>
    private static string? Fingerprint(string path, long size)
    {
        const int chunk = 256 * 1024;
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var head = new byte[(int)Math.Min(chunk, size)];
            stream.ReadExactly(head, 0, head.Length);
            sha.TransformBlock(head, 0, head.Length, null, 0);

            if (size > chunk * 2L)
            {
                var tail = new byte[chunk];
                stream.Seek(-chunk, SeekOrigin.End);
                stream.ReadExactly(tail, 0, chunk);
                sha.TransformBlock(tail, 0, chunk, null, 0);
            }

            var length = BitConverter.GetBytes(size);
            sha.TransformFinalBlock(length, 0, length.Length);
            return sha.Hash is { } hash ? Convert.ToHexString(hash) : null;
        }
        catch { return null; }
    }
}
