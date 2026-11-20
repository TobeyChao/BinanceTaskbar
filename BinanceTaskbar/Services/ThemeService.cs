using Microsoft.Win32;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace BinanceTaskbar.Services;

public sealed record ThemePalette(
    Brush WindowBackground,
    Brush BorderBrush,
    Brush PrimaryText,
    Brush SecondaryText,
    Brush Separator,
    Brush Up,
    Brush Down,
    Brush Warning);

public static class ThemeService
{
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("SystemUsesLightTheme", 0)) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static ThemePalette Create(string configuredTheme, double opacity)
    {
        var useLight = configuredTheme switch
        {
            "light" => true,
            "dark" => false,
            _ => IsSystemLightTheme()
        };

        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0.55, 1.0) * 255);
        if (useLight)
        {
            return new ThemePalette(
                Brush(alpha, 248, 249, 251),
                Brush(36, 15, 19, 26),
                Brush(255, 18, 22, 29),
                Brush(155, 34, 40, 51),
                Brush(28, 15, 19, 26),
                Brush(255, 8, 145, 96),
                Brush(255, 218, 55, 72),
                Brush(255, 219, 143, 10));
        }

        return new ThemePalette(
            Brush(alpha, 17, 21, 29),
            Brush(38, 255, 255, 255),
            Brush(255, 248, 250, 253),
            Brush(155, 205, 211, 222),
            Brush(28, 255, 255, 255),
            Brush(255, 79, 220, 149),
            Brush(255, 255, 111, 126),
            Brush(255, 240, 185, 11));
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}


