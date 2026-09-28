namespace MediaForge.Services;

public static class ToolLocator
{
    public static string ApplicationDirectory => AppContext.BaseDirectory;
    public static string LegacyDirectory
    {
        get
        {
            for (var directory = new DirectoryInfo(ApplicationDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "kontext und alt");
                if (Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(Environment.CurrentDirectory, "kontext und alt");
        }
    }
    /// <summary>Ausweichordner für heruntergeladene Werkzeuge, falls neben der Exe nicht geschrieben werden darf.</summary>
    public static string ToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge", "tools");

    /// <summary>Ablageort für Werkzeug-Updates: neben der Exe (portabel), sonst <see cref="ToolDirectory"/>.</summary>
    public static string DefaultToolDirectory => IsWritable(ApplicationDirectory) ? ApplicationDirectory : ToolDirectory;

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".mediaforge_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sucht ein Werkzeug; ein in den Einstellungen hinterlegter Pfad hat Vorrang.</summary>
    public static string? Find(string executable, string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var value = configured.Trim().Trim('"');
            if (Directory.Exists(value)) value = Path.Combine(value, executable);
            var preferred = Probe(value);
            if (preferred is not null) return preferred;
        }
        return Probe(executable);
    }

    private static string? Probe(string executable)
    {
        if (Path.IsPathRooted(executable) && File.Exists(executable)) return Path.GetFullPath(executable);
        var names = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { executable }
            : new[] { executable, executable + ".exe" };
        var roots = new[]
        {
            ApplicationDirectory,
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(ApplicationDirectory, "..")),
            LegacyDirectory,
            Path.Combine(ApplicationDirectory, "Weiteres"),
            Path.Combine(ApplicationDirectory, "ffmpeg", "bin"),
            ToolDirectory
        };
        foreach (var root in roots)
        foreach (var name in names)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var root in paths)
        foreach (var name in names)
        {
            try
            {
                var candidate = Path.Combine(root.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* Ungültiger PATH-Eintrag. */ }
        }
        return null;
    }
}
