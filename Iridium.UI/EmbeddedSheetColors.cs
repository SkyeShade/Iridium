using System.Globalization;

namespace Iridium.UI;

public static class EmbeddedSheetColors
{
    private const string StructuralBackground = "#292D35";
    private const string LightForeground = "#FFFFFF";
    private const string DarkForeground = "#000000";

    public static string? Background(string? source, bool hasContent, bool isMerged)
    {
        if (!TryParse(source, out var color)) return null;
        var (hue, saturation, lightness) = ToHsl(color);
        if (!hasContent && !isMerged && saturation <= .08 && lightness >= .88)
            return StructuralBackground;
        return ToHex(color);
    }

    public static string? Foreground(string? source, string? finalBackground, bool backgroundTransformed = false)
    {
        if (!TryParse(finalBackground, out var background))
            return TryParse(source, out var sourceOnly) ? ToHex(sourceOnly) : null;
        if (!TryParse(source, out var foreground)) return BestFallback(background);
        if (!backgroundTransformed || Contrast(foreground, background) >= 3)
            return ToHex(foreground);
        return BestFallback(background);
    }

    private static string BestFallback(Rgb background)
    {
        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        return Contrast(white, background) >= Contrast(black, background) ? LightForeground : DarkForeground;
    }

    public static string? Border(string? source)
    {
        if (!TryParse(source, out var color)) return null;
        var (_, saturation, lightness) = ToHsl(color);
        if (saturation <= .08 && lightness >= .9) return "#7C8492";
        return ToHex(color);
    }

    public static double ContrastRatio(string foreground, string background) =>
        TryParse(foreground, out var fg) && TryParse(background, out var bg) ? Contrast(fg, bg) : 0;

    private static double Contrast(Rgb first, Rgb second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Luminance(Rgb color)
    {
        static double Channel(byte value) { var v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Rgb color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2; var delta = max - min;
        if (delta == 0) return (0, 0, lightness);
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, saturation, lightness);
    }

    private static bool TryParse(string? value, out Rgb color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#' ||
            !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, null, out var b)) return false;
        color = new(r, g, b); return true;
    }

    private static string ToHex(Rgb color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private readonly record struct Rgb(byte R, byte G, byte B);
}
