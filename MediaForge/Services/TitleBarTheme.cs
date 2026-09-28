using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MediaForge.Services;

/// <summary>
/// Färbt die Systemtitelleiste im Ton der App ein, damit sie nicht als weißer Streifen
/// über der dunklen Oberfläche steht. Windows 11 (Build 22000+) setzt das um; ältere
/// Versionen ignorieren die Attribute stillschweigend und behalten ihre Standardleiste.
/// </summary>
public static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Hängt die Einfärbung an das Fenster; sie greift, sobald das Fensterhandle existiert.
    /// </summary>
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) => Paint(window);
    }

    private static void Paint(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        try
        {
            // Dunkelmodus zuerst: davon hängen die Glyphen von Minimieren/Maximieren/Schließen ab.
            var dark = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var caption = ToColorRef(Resource("PowerCard", Color.FromRgb(0x1B, 0x22, 0x2D)));
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));

            var text = ToColorRef(Resource("PowerText", Color.FromRgb(0xEE, 0xF2, 0xF7)));
            DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Ohne dwmapi.dll bleibt die Leiste eben wie sie ist.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Holt die Farbe aus dem Theme, damit die Leiste mitzieht, wenn sich der Ton dort ändert.</summary>
    private static Color Resource(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    /// <summary>DWM erwartet ein COLORREF, also 0x00BBGGRR statt des gewohnten RGB.</summary>
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
