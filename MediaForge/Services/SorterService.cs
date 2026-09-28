using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaForge.Core;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>Ein bereits einsortierter Eintrag — merkt sich Ziel und Zielpfad für Fortschritt und Rückgängig.</summary>
public sealed class SorterProgressEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public bool Copied { get; set; }
    public DateTime SortedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SorterProgress
{
    public string Source { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<SorterProgressEntry> Entries { get; set; } = [];
}

/// <summary>
/// Ein einmal gelesener Index des Backup-Baums. Gruppiert wird nach dem letzten Ordnernamen,
/// sodass die spätere Zuordnung keine erneuten Laufwerkszugriffe braucht.
/// </summary>
public sealed class BackupFolderIndex
{
    internal BackupFolderIndex(string root, Dictionary<string, List<string>> foldersByName)
    {
        Root = root;
        FoldersByName = foldersByName;
    }

    public string Root { get; }
    internal Dictionary<string, List<string>> FoldersByName { get; }
}

/// <summary>
/// Liest den Startordner ein, legt Dateien in Zielbereichen ab und hält den Fortschritt je Startordner fest.
/// Verschobene Dateien verschwinden ohnehin aus dem Startordner; die Fortschrittsdatei deckt Kopiermodus und Rückgängig ab.
/// </summary>
public static class SorterService
{
    /// <summary>Reihenfolge der automatisch vergebenen Tasten — erst die Ziffern, dann die obere Buchstabenreihe.</summary>
    public const string KeyPool = "1234567890qwertzuiop";

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".ogv"
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".jfif"
    };

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store"
    };

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".part", ".crdownload", ".lnk"
    };

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static SorterItemKind KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (VideoExtensions.Contains(extension)) return SorterItemKind.Video;
        return ImageExtensions.Contains(extension) ? SorterItemKind.Image : SorterItemKind.File;
    }

    /// <summary>
    /// Sammelt alle Dateien des Startordners. Zielordner, die im Startordner liegen, bleiben außen vor,
    /// damit bereits einsortierte Dateien nicht erneut auftauchen.
    /// </summary>
    public static List<SorterItem> Scan(string source, bool includeSubfolders, IEnumerable<SorterTarget> targets, string? backupDirectory = null)
    {
        var items = new List<SorterItem>();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) return items;

        var root = Path.GetFullPath(source);
        var blocked = new List<string>();
        if (!string.IsNullOrWhiteSpace(backupDirectory)) blocked.Add(Path.GetFullPath(backupDirectory));
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Folder)) continue;
            try
            {
                var folder = Path.GetFullPath(target.Folder);
                if (IsInside(folder, root) && !PathEquals(folder, root)) blocked.Add(folder);
            }
            catch { /* Ungültiger Zielpfad: beim Einlesen übergehen. */ }
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*",
                new EnumerationOptions { RecurseSubdirectories = includeSubfolders, IgnoreInaccessible = true });
        }
        catch { return items; }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (SkippedNames.Contains(name) || name.StartsWith("~$", StringComparison.Ordinal)) continue;
            if (SkippedExtensions.Contains(Path.GetExtension(file))) continue;

            var directory = Path.GetDirectoryName(file) ?? root;
            if (blocked.Any(folder => IsInside(directory, folder))) continue;

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            }
            catch { continue; }

            var relative = Path.GetRelativePath(root, file);
            var subfolder = Path.GetDirectoryName(relative) ?? string.Empty;
            items.Add(new SorterItem
            {
                FullPath = file,
                Name = name,
                RelativePath = relative,
                Subfolder = subfolder == "." ? string.Empty : subfolder,
                Size = info.Length,
                Kind = KindOf(file),
                Extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant()
            });
        }

        items.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    /// <summary>Legt die Datei im Zielbereich ab; belegte Namen bekommen automatisch eine Nummer.</summary>
    public static async Task<string> PlaceAsync(SorterItem item, SorterTarget target, bool copy, bool preserveSubfolders)
    {
        var directory = preserveSubfolders && item.Subfolder.Length > 0
            ? Path.Combine(target.Folder, item.Subfolder)
            : target.Folder;
        Directory.CreateDirectory(directory);
        var destination = Formatters.UniquePath(Path.Combine(directory, item.Name));
        await RetryAsync(() =>
        {
            if (copy) File.Copy(item.FullPath, destination);
            else File.Move(item.FullPath, destination);
        });
        return destination;
    }

    /// <summary>Liest den Backup-Baum genau einmal und erstellt einen schnellen Suchindex.</summary>
    public static BackupFolderIndex CreateBackupFolderIndex(string backupRoot, IEnumerable<string>? targetFolders = null)
    {
        var root = Path.GetFullPath(backupRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Backup-Ordner nicht gefunden.");
        var relevantNames = targetFolders?.Select(folder => Path.GetFileName(Normalize(folder)))
            .Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true, IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            var name = Path.GetFileName(folder);
            if (relevantNames is { Count: > 0 } && !relevantNames.Contains(name)) continue;
            if (!folders.TryGetValue(name, out var matches)) folders[name] = matches = [];
            matches.Add(folder);
        }
        return new BackupFolderIndex(root, folders);
    }

    /// <summary>Findet im vorbereiteten Index den am besten passenden Zielordner anhand gemeinsamer Pfadenden.</summary>
    public static string ResolveBackupFolder(BackupFolderIndex index, string targetFolder)
    {
        var root = index.Root;
        var target = Normalize(Path.GetFullPath(targetFolder));
        if (IsInside(root, target) || IsInside(target, root))
            throw new IOException("Backup-Ordner und Sortierziel müssen getrennte Ordner sein.");
        if (TryResolveDirectBackupFolder(root, target, out var direct)) return direct;
        var parts = target.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (!index.FoldersByName.TryGetValue(Path.GetFileName(target), out var namedFolders))
            return Path.Combine(root, Path.GetFileName(target));
        var candidates = namedFolders.Select(folder =>
        {
            var relative = Path.GetRelativePath(root, folder).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var score = 0;
            while (score < Math.Min(parts.Length, relative.Length) &&
                   parts[^(score + 1)].Equals(relative[^(score + 1)], StringComparison.OrdinalIgnoreCase)) score++;
            return (Folder: folder, Score: score);
        }).Where(candidate => candidate.Score > 0).ToList();
        if (candidates.Count == 0) return Path.Combine(root, Path.GetFileName(target));
        var best = candidates.Where(candidate => candidate.Score == candidates.Max(c => c.Score)).ToList();
        if (best.Count != 1) throw new IOException($"Mehrere passende Backup-Unterordner für „{Path.GetFileName(target)}“. Bitte die Ordnerstruktur eindeutig machen.");
        return best[0].Folder;
    }

    /// <summary>
    /// Erkennt eine direkt gespiegelte Struktur ohne rekursiven Scan. Dabei gewinnt der längste
    /// existierende Pfad, z. B. Backup\Sammlung\Fotos vor Backup\Fotos.
    /// </summary>
    public static bool TryResolveDirectBackupFolder(string backupRoot, string targetFolder, out string folder)
    {
        var root = Path.GetFullPath(backupRoot);
        var target = Normalize(Path.GetFullPath(targetFolder));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Backup-Ordner nicht gefunden.");
        if (IsInside(root, target) || IsInside(target, root))
            throw new IOException("Backup-Ordner und Sortierziel müssen getrennte Ordner sein.");
        var pathRoot = Path.GetPathRoot(target) ?? string.Empty;
        var relative = target[pathRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start < parts.Length; start++)
        {
            var candidate = Path.Combine([root, .. parts[start..]]);
            if (!Directory.Exists(candidate)) continue;
            folder = candidate;
            return true;
        }
        folder = string.Empty;
        return false;
    }

    /// <summary>Kompatibler Einzelaufruf; für mehrere Dateien sollte derselbe Index wiederverwendet werden.</summary>
    public static string ResolveBackupFolder(string backupRoot, string targetFolder) =>
        ResolveBackupFolder(CreateBackupFolderIndex(backupRoot), targetFolder);

    public static Task<string> BackupAsync(string destination, string targetFolder, string backupRoot) =>
        BackupToFolderAsync(destination, targetFolder, ResolveBackupFolder(backupRoot, targetFolder));

    public static Task<string> BackupToFolderAsync(string destination, string targetFolder, string resolvedBackupFolder) => Task.Run(() =>
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(targetFolder), Path.GetFullPath(destination));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("Die Datei liegt außerhalb des Sortierziels.");
        var path = Formatters.UniquePath(Path.Combine(resolvedBackupFolder, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // File.Copy nutzt unter Windows den nativen, für große Dateien optimierten Kopierpfad.
        File.Copy(destination, path, overwrite: false);
        return path;
    });

    /// <summary>Nimmt eine Ablage zurück: verschobene Dateien wandern zurück, Kopien werden wieder entfernt. Backups bleiben erhalten.</summary>
    public static async Task UndoAsync(SorterProgressEntry entry)
    {
        if (!File.Exists(entry.Destination))
            throw new FileNotFoundException("Die abgelegte Datei liegt nicht mehr am Zielort.", entry.Destination);
        if (entry.Copied)
        {
            await RetryAsync(() => File.Delete(entry.Destination));
            return;
        }
        if (File.Exists(entry.SourcePath))
            throw new IOException("Im Startordner liegt bereits wieder eine Datei mit diesem Namen.");
        var directory = Path.GetDirectoryName(entry.SourcePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await RetryAsync(() => File.Move(entry.Destination, entry.SourcePath));
    }

    /// <summary>Fortschrittsdatei je Startordner — Name plus Kurz-Prüfsumme, damit gleiche Ordnernamen sich nicht überschreiben.</summary>
    public static string ProgressFile(string source)
    {
        var root = string.IsNullOrWhiteSpace(source) ? "unbekannt" : Path.GetFullPath(source);
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
            .Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (name.Length == 0) name = "ordner";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..8];
        return Path.Combine(Path.GetDirectoryName(SettingsService.FilePath)!, "sorter", $"{name}-{hash}.json");
    }

    public static SorterProgress LoadProgress(string source)
    {
        try
        {
            var path = ProgressFile(source);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<SorterProgress>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.Source = source;
                    return loaded;
                }
            }
        }
        catch { /* Beschädigte Fortschrittsdatei: mit leerem Stand weitermachen. */ }
        return new SorterProgress { Source = source };
    }

    public static bool SaveProgress(SorterProgress progress)
    {
        try
        {
            progress.UpdatedUtc = DateTime.UtcNow;
            var path = ProgressFile(progress.Source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(progress, Options));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Der Player gibt die Datei erst kurz nach dem Schließen frei — deshalb einige kurze Versuche.</summary>
    private static async Task RetryAsync(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 14 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(60);
            }
        }
    }

    private static bool IsInside(string candidate, string folder)
    {
        var normalized = Normalize(folder);
        var value = Normalize(candidate);
        return value.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string a, string b) =>
        Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
