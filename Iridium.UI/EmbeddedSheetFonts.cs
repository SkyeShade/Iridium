using System.Text.RegularExpressions;

namespace Iridium.UI;

public static partial class EmbeddedSheetFonts
{
    public static string? Family(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var family = source.Split(',')[0].Trim().Trim('"', '\'');
        if (family.Length is < 1 or > 64 || !SafeFamily().IsMatch(family)) return null;
        return family.ToLowerInvariant() switch
        {
            "arial" => "Arial, sans-serif",
            "roboto" => "Roboto, Arial, sans-serif",
            "times new roman" => "\"Times New Roman\", serif",
            "georgia" => "Georgia, serif",
            "courier new" => "\"Courier New\", monospace",
            "consolas" => "Consolas, monospace",
            _ => $"\"{family}\", var(--font-family, sans-serif)"
        };
    }

    public static double? Size(double? pixels) => pixels is null ? null : Math.Round(Math.Clamp(pixels.Value, 8, 48), 2);
    public static int? Weight(int? weight) => weight is null ? null : Math.Clamp(weight.Value, 100, 900);

    [GeneratedRegex(@"^[\p{L}\p{N} _-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 50)]
    private static partial Regex SafeFamily();
}
