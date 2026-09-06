using Iridium.Protocol;

namespace Iridium.UI;

public static class EmbeddedSheetCellPresentation
{
    public static string CssClass(EmbeddedSheetCellDto cell) => string.Join(' ',
        $"sheet-h-{cell.HorizontalAlignment.ToString().ToLowerInvariant()}",
        $"sheet-v-{cell.VerticalAlignment.ToString().ToLowerInvariant()}",
        $"sheet-fg-{cell.ForegroundColor.ToString().ToLowerInvariant()}",
        $"sheet-bg-{cell.BackgroundColor.ToString().ToLowerInvariant()}",
        $"sheet-bt-{cell.TopBorder.ToString().ToLowerInvariant()}",
        $"sheet-br-{cell.RightBorder.ToString().ToLowerInvariant()}",
        $"sheet-bb-{cell.BottomBorder.ToString().ToLowerInvariant()}",
        $"sheet-bl-{cell.LeftBorder.ToString().ToLowerInvariant()}",
        cell.Bold ? "sheet-bold" : null, cell.Italic ? "sheet-italic" : null,
        cell.Underline ? "sheet-underline" : null, cell.IsCheckbox ? "sheet-checkbox" : null,
        IsNumeric(cell) ? "sheet-numeric" : null,
        cell.WrapText == true ? "sheet-wrap" : "sheet-nowrap",
        $"sheet-font-{cell.FontSize.ToString().ToLowerInvariant()}");

    public static string Style(EmbeddedSheetCellDto cell, int cellHeight)
    {
        var values = new List<string>(16)
        {
            $"--sheet-cell-height:{cellHeight}px",
            $"text-align:{Horizontal(cell.HorizontalAlignment)}",
            $"vertical-align:{Vertical(cell.VerticalAlignment)}"
        };
        var hasContent = !string.IsNullOrWhiteSpace(cell.DisplayValue) || cell.IsCheckbox;
        var background = EmbeddedSheetColors.Background(cell.BackgroundHex, hasContent,
            cell.RowSpan > 1 || cell.ColumnSpan > 1);
        var transformed = background is not null && !string.Equals(background, cell.BackgroundHex,
            StringComparison.OrdinalIgnoreCase);
        if (background is not null) values.Add($"background-color:{background}");
        if (EmbeddedSheetColors.Foreground(cell.ForegroundHex, background, transformed) is { } foreground)
            values.Add($"color:{foreground}");
        if (EmbeddedSheetFonts.Family(cell.FontFamily) is { } family) values.Add($"font-family:{family}");
        if (EmbeddedSheetFonts.Size(cell.FontSizePx) is { } size) values.Add($"font-size:{size}px");
        if (EmbeddedSheetFonts.Weight(cell.FontWeight) is { } weight) values.Add($"font-weight:{weight}");
        if (cell.IndentLevel > 0) values.Add($"padding-inline-start:{4 + cell.IndentLevel * 10}px");
        AddBorder(values, "top", cell.TopBorder, cell.TopBorderColor);
        AddBorder(values, "right", cell.RightBorder, cell.RightBorderColor);
        AddBorder(values, "bottom", cell.BottomBorder, cell.BottomBorderColor);
        AddBorder(values, "left", cell.LeftBorder, cell.LeftBorderColor);
        return string.Join(';', values);
    }

    private static string Horizontal(EmbeddedDocumentTextAlignment alignment) => alignment switch
    {
        EmbeddedDocumentTextAlignment.Center => "center",
        EmbeddedDocumentTextAlignment.End => "right",
        EmbeddedDocumentTextAlignment.Justify => "justify",
        _ => "left"
    };

    private static string Vertical(EmbeddedSheetVerticalAlignment alignment) => alignment switch
    {
        EmbeddedSheetVerticalAlignment.Top => "top",
        EmbeddedSheetVerticalAlignment.Middle => "middle",
        _ => "bottom"
    };

    private static bool IsNumeric(EmbeddedSheetCellDto cell) => !cell.IsCheckbox &&
        double.TryParse(cell.RawValue, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static void AddBorder(List<string> values, string side, EmbeddedSheetBorderStyle style, string? color)
    {
        if (style == EmbeddedSheetBorderStyle.None) return;
        var (width, lineStyle) = style switch
        {
            EmbeddedSheetBorderStyle.Medium => (2, "solid"),
            EmbeddedSheetBorderStyle.Thick => (3, "solid"),
            EmbeddedSheetBorderStyle.Double => (3, "double"),
            EmbeddedSheetBorderStyle.Dashed => (1, "dashed"),
            EmbeddedSheetBorderStyle.Dotted => (1, "dotted"),
            _ => (1, "solid")
        };
        var safeColor = EmbeddedSheetColors.Border(color) ?? "var(--text-muted, #9AA0A6)";
        values.Add($"border-{side}:{width}px {lineStyle} {safeColor}");
    }
}
