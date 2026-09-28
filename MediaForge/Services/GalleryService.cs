using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>
/// Liest die registrierten Ordner ein und erzeugt Vorschaubilder.
/// Die Galerie kopiert nichts und benennt nichts um — sie schaut nur hinein.
/// </summary>
public static class GalleryService
{
    /// <summary>Kantenlänge der zwischengespeicherten Vorschaubilder.</summary>
    public const int ThumbnailSize = 320;

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store"
    };

    /// <summary>Sammelt Bilder und Videos aus allen registrierten Ordnern, Dubletten über Pfad ausgeschlossen.</summary>
    public static List<GalleryItem> Scan(IEnumerable<string> roots, CancellationToken token = default)
    {
        var items = new List<GalleryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            var fullRoot = Path.GetFullPath(root);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(fullRoot, "*",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch { continue; }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (SkippedNames.Contains(name) || name.StartsWith("~$", StringComparison.Ordinal)) continue;

                var extension = Path.GetExtension(file);
                GalleryKind kind;
                if (SorterService.ImageExtensions.Contains(extension)) kind = GalleryKind.Image;
                else if (SorterService.VideoExtensions.Contains(extension)) kind = GalleryKind.Video;
                else continue;

                if (!seen.Add(file)) continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                }
                catch { continue; }

                var relative = Path.GetRelativePath(fullRoot, file);
                var subfolder = Path.GetDirectoryName(relative) ?? string.Empty;
                items.Add(new GalleryItem
                {
                    FullPath = file,
                    Name = name,
                    RootPath = fullRoot,
                    Subfolder = subfolder == "." ? string.Empty : subfolder,
                    Kind = kind,
                    Extension = extension.TrimStart('.').ToUpperInvariant(),
                    Size = info.Length,
                    ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                    TakenAt = kind == GalleryKind.Image
                        ? DateTaken(file) ?? info.LastWriteTime
                        : info.LastWriteTime
                });
            }
        }

        items.Sort((a, b) => b.TakenAt.CompareTo(a.TakenAt));
        return items;
    }

    /// <summary>Aufnahmedatum aus den Bilddaten; nur die Kopfdaten werden gelesen, nicht das ganze Bild.</summary>
    private static DateTime? DateTaken(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.Metadata is not BitmapMetadata metadata) return null;
            var text = metadata.DateTaken;
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed) ||
                   DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                ? parsed
                : null;
        }
        catch { return null; }
    }

    /// <summary>Überschrift einer Tagesgruppe — heute und gestern beim Namen, die letzte Woche als Wochentag.</summary>
    public static string DayHeader(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Heute";
        if (day == today.AddDays(-1)) return "Gestern";
        if (day > today.AddDays(-7)) return day.ToString("dddd", CultureInfo.CurrentCulture);
        return day.Year == today.Year
            ? day.ToString("ddd, d. MMM", CultureInfo.CurrentCulture)
            : day.ToString("ddd, d. MMM yyyy", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------ Vorschaubilder

    /// <summary>Ablageort des Vorschaubilds; ändert sich die Datei, ändert sich der Name und das alte Bild verfällt.</summary>
    public static string ThumbnailPath(GalleryItem item)
    {
        var seed = $"{item.Key}|{item.ModifiedTicks}|{item.Size}|{ThumbnailSize}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(seed)));
        return Path.Combine(GalleryStore.ThumbnailDirectory, hash + ".jpg");
    }

    /// <summary>
    /// Liefert das Vorschaubild — aus dem Zwischenspeicher, sonst frisch erzeugt.
    /// Bilder werden verkleinert, Videos über ein Einzelbild von ffmpeg.
    /// </summary>
    public static async Task<ImageSource?> ThumbnailAsync(GalleryItem item, FfmpegService ffmpeg,
        CancellationToken token = default)
    {
        var cache = ThumbnailPath(item);
        if (File.Exists(cache)) return Load(cache);

        try
        {
            Directory.CreateDirectory(GalleryStore.ThumbnailDirectory);
            if (item.Kind == GalleryKind.Image)
            {
                var source = Load(item.FullPath);
                if (source is null) return null;
                WriteCache(source, cache);
                return source;
            }

            var seconds = item.DurationMs > 0 ? Math.Min(3, item.DurationMs / 2000d) : 1;
            var frame = await ffmpeg.ExtractFrameAsync(item.FullPath, seconds, ThumbnailSize, token)
                        ?? await ffmpeg.ExtractFrameAsync(item.FullPath, 0, ThumbnailSize, token);
            if (frame is null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(frame);
            image.EndInit();
            image.Freeze();
            WriteCache(image, cache);
            return image;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Lädt ein Bild verkleinert und eingefroren, damit es aus dem Hintergrund an die Oberfläche darf.</summary>
    public static ImageSource? Load(string path, int decodeWidth = ThumbnailSize)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static void WriteCache(ImageSource source, string cache)
    {
        try
        {
            if (source is not BitmapSource bitmap) return;
            var encoder = new JpegBitmapEncoder { QualityLevel = 84 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(cache);
            encoder.Save(stream);
        }
        catch { /* Der Zwischenspeicher ist Komfort; ohne ihn wird das Bild eben neu erzeugt. */ }
    }

    /// <summary>Entfernt alle zwischengespeicherten Vorschaubilder.</summary>
    public static long ClearThumbnails()
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(GalleryStore.ThumbnailDirectory)) return 0;
            foreach (var file in Directory.EnumerateFiles(GalleryStore.ThumbnailDirectory, "*.jpg"))
            {
                try
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                }
                catch { /* Belegte Datei überspringen. */ }
            }
        }
        catch { /* Kein Zugriff auf den Zwischenspeicher: nichts zu tun. */ }
        return freed;
    }
}
