using System.Globalization;
using System.Text;

namespace MediaForge.Core;

public static class Formatters
{
    public static string Time(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    public static double? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        if (!parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return null;
        var numbers = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return numbers.Length switch
        {
            1 => numbers[0],
            2 => numbers[0] * 60 + numbers[1],
            3 => numbers[0] * 3600 + numbers[1] * 60 + numbers[2],
            _ => null
        };
    }

    /// <summary>Millisekunden als HH:MM:SS bzw. HH:MM:SS.mmm — die Anzeigeform der Metadaten.</summary>
    public static string TimeFromMs(long milliseconds, bool withMilliseconds = false)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        var text = $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        return withMilliseconds ? $"{text}.{span.Milliseconds:000}" : text;
    }

    /// <summary>Liest HH:MM:SS(.mmm), MM:SS oder SS und liefert Millisekunden.</summary>
    public static long? ParseTimeToMs(string? value) =>
        ParseTime(value) is { } seconds ? ToMs(seconds) : null;

    public static long ToMs(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 1000);

    public static double ToSeconds(long milliseconds) => Math.Max(0, milliseconds) / 1000d;

    public static string Size(long? bytes)
    {
        if (bytes is null) return "?";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{(long)value} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string MiddleEllipsis(string? value, int maxLength = 56)
    {
        value ??= string.Empty;
        if (value.Length <= maxLength) return value;
        var keep = maxLength - 1;
        var head = keep / 2;
        return value[..head] + "…" + value[^(keep - head)..];
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static string Atempo(double factor)
    {
        var filters = new List<string>();
        while (factor > 2) { filters.Add("atempo=2.0"); factor /= 2; }
        while (factor < .5) { filters.Add("atempo=0.5"); factor /= .5; }
        filters.Add($"atempo={factor.ToString("0.######", CultureInfo.InvariantCulture)}");
        return string.Join(',', filters);
    }

    public static IReadOnlyList<string> ParseCommandLine(string commandLine)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        char quote = '\0';
        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if ((ch is '\"' or '\'') && (!quoted || ch == quote))
            {
                if (!quoted) { quoted = true; quote = ch; }
                else { quoted = false; quote = '\0'; }
                continue;
            }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
                continue;
            }
            token.Append(ch);
        }
        if (quoted) throw new FormatException("Nicht geschlossene Anführungszeichen.");
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }
}
