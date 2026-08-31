using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Iridium.Protocol;

namespace Iridium.Server.Embeds;

public sealed partial class GoogleSheetsHtmlParser
{
    public const int MaximumRows = 5_000;
    public const int MaximumColumns = 500;
    public const int MaximumCells = 200_000;
    public const int MaximumTabs = 20;

    public IReadOnlyList<(string Id, string Name)> DiscoverTabs(string source)
    {
        var document = new HtmlParser().ParseDocument(source);
        var tabs = new List<(string Id, string Name)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in document.QuerySelectorAll("a[href]") )
        {
            var match = Regex.Match(link.GetAttribute("href") ?? string.Empty, @"(?:[?&#]|&amp;)gid=(\d{1,20})(?:&|$)");
            if (!match.Success || !seen.Add(match.Groups[1].Value)) continue;
            tabs.Add((match.Groups[1].Value, NormalizeValue(link.TextContent) is { Length: > 0 } name
                ? name : $"Sheet {tabs.Count + 1}"));
            if (tabs.Count == MaximumTabs) break;
        }
        return tabs;
    }

    public EmbeddedSheetDto? Parse(string source, EmbeddedContentConfiguration configuration)
    {
        var document = new HtmlParser().ParseDocument(source);
        var tables = document.QuerySelectorAll("table.waffle, table").Where(table =>
            table.QuerySelector("tr") is not null && table.ParentElement?.Closest("table") is null).ToArray();
        if (tables.Length == 0) return null;
        var classStyles = Styles(document);
        var tabs = new List<EmbeddedSheetTabDto>();
        var totalCells = 0;
        for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
        {
            var table = tables[tableIndex];
            var rows = new List<EmbeddedSheetRowDto>();
            var occupied = new HashSet<(int Row, int Column)>();
            var rowElements = table.QuerySelectorAll(":scope > tbody > tr, :scope > tr");
            for (var rowIndex = 0; rowIndex < rowElements.Length && rowIndex < MaximumRows; rowIndex++)
            {
                var row = rowElements[rowIndex];
                var cells = new List<EmbeddedSheetCellDto>();
                var column = 0;
                foreach (var cell in row.Children.Where(value => value.LocalName is "td" or "th"))
                {
                    while (occupied.Contains((rowIndex, column))) column++;
                    if (column >= MaximumColumns || ++totalCells > MaximumCells)
                        throw new GoogleSheetsTooLargeException();
                    var rowSpan = Span(cell, "rowspan", MaximumRows - rowIndex);
                    var columnSpan = Span(cell, "colspan", MaximumColumns - column);
                    for (var r = rowIndex; r < rowIndex + rowSpan; r++)
                        for (var c = column; c < column + columnSpan; c++) occupied.Add((r, c));
                    var style = CombinedStyle(cell, classStyles);
                    var checkbox = cell.QuerySelector("input[type=checkbox]");
                    var value = checkbox is null ? NormalizeValue(cell.TextContent) :
                        checkbox.HasAttribute("checked") ? "☑" : "☐";
                    var link = SafeLink(cell.QuerySelector("a[href]")?.GetAttribute("href"));
                    var backgroundHex = HexColor(style, "background(?:-color)?");
                    var effectiveTextStyle = EffectiveTextStyle(cell, style, classStyles);
                    var foregroundHex = HexColor(effectiveTextStyle, "color");
                    cells.Add(new(rowIndex, column, value, rowSpan, columnSpan,
                        Bold(effectiveTextStyle, cell), Italic(effectiveTextStyle, cell), Underline(effectiveTextStyle, cell),
                        Horizontal(style), Vertical(style), Foreground(style), Background(style),
                        Border(style, "top"), Border(style, "right"), Border(style, "bottom"),
                        Border(style, "left"), link, checkbox is not null,
                        checkbox is null ? null : checkbox.HasAttribute("checked"), FontSize(effectiveTextStyle),
                        foregroundHex, backgroundHex, BorderColor(style, "top"), BorderColor(style, "right"),
                        BorderColor(style, "bottom"), BorderColor(style, "left"), null,
                        FontFamily(effectiveTextStyle), FontSizePixels(effectiveTextStyle), FontWeight(effectiveTextStyle),
                        WrapText(style)));
                    column += columnSpan;
                }
                rows.Add(new(Dimension(row.GetAttribute("style"), "height", 12, 400), cells));
            }
            var widths = table.QuerySelectorAll("col").Take(MaximumColumns)
                .Select(col => Dimension(col.GetAttribute("style"), "width", 24, 600) ?? 100).ToArray();
            var id = table.GetAttribute("data-gid") ?? (tableIndex == 0 ? configuration.TabId : null) ??
                tableIndex.ToString(CultureInfo.InvariantCulture);
            var name = table.GetAttribute("data-sheet-name") ?? table.GetAttribute("aria-label") ??
                (tables.Length == 1 ? document.Title : null) ?? $"Sheet {tableIndex + 1}";
            tabs.Add(new(id, name.Trim(), rows, widths));
        }
        var defaultId = configuration.TabId is { } requested && tabs.Any(tab => tab.Id == requested)
            ? requested : tabs[0].Id;
        return new(configuration.SourceId, document.Title?.Trim(), tabs, defaultId);
    }

    private static Dictionary<string, string> Styles(IDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var style in document.QuerySelectorAll("style"))
            foreach (Match match in CssRule().Matches(style.TextContent))
                foreach (var selector in match.Groups[1].Value.Split(','))
                    if (selector.Trim() is { Length: > 1 } name && name[0] == '.' &&
                        name[1..].All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_'))
                        result[name[1..]] = match.Groups[2].Value;
        return result;
    }

    private static string CombinedStyle(IElement element, IReadOnlyDictionary<string, string> classes)
    {
        var values = element.ClassList.Where(classes.ContainsKey).Select(name => classes[name]).ToList();
        if (element.GetAttribute("style") is { } inline) values.Add(inline);
        return string.Join(';', values).ToLowerInvariant();
    }
    private static string EffectiveTextStyle(IElement cell, string cellStyle,
        IReadOnlyDictionary<string, string> classes)
    {
        // Published Sheets may attach the final font color to a generated class on a nested span.
        foreach (var element in cell.QuerySelectorAll("*").Reverse())
            if (!string.IsNullOrWhiteSpace(element.TextContent) && CombinedStyle(element, classes) is { Length: > 0 } nested &&
                (HexColor(nested, "color") is not null || FontFamily(nested) is not null || FontSizePixels(nested) is not null))
                return $"{cellStyle};{nested}";
        return cellStyle;
    }
    private static int Span(IElement element, string name, int maximum) =>
        int.TryParse(element.GetAttribute(name), out var value) ? Math.Clamp(value, 1, maximum) : 1;
    private static int? Dimension(string? style, string name, int minimum, int maximum)
    {
        if (style is null) return null;
        var match = Regex.Match(style, $@"(?:^|;)\s*{name}\s*:\s*(\d+(?:\.\d+)?)px", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp((int)Math.Round(value), minimum, maximum) : null;
    }
    private static string NormalizeValue(string value) => Regex.Replace(value.Replace('\u00a0', ' '), @"\s+", " ").Trim();
    private static bool Bold(string style, IElement cell) => cell.LocalName == "th" ||
        style.Contains("font-weight:bold") || Regex.IsMatch(style, @"font-weight\s*:\s*[6-9]00");
    private static bool Italic(string style, IElement cell) => style.Contains("font-style:italic") || cell.QuerySelector("i,em") is not null;
    private static bool Underline(string style, IElement cell) => style.Contains("text-decoration:underline") || cell.QuerySelector("u") is not null;
    private static EmbeddedDocumentTextAlignment Horizontal(string style) => style.Contains("text-align:center")
        ? EmbeddedDocumentTextAlignment.Center : style.Contains("text-align:right")
            ? EmbeddedDocumentTextAlignment.End : EmbeddedDocumentTextAlignment.Start;
    private static EmbeddedSheetVerticalAlignment Vertical(string style) => style.Contains("vertical-align:top")
        ? EmbeddedSheetVerticalAlignment.Top : style.Contains("vertical-align:bottom")
            ? EmbeddedSheetVerticalAlignment.Bottom : EmbeddedSheetVerticalAlignment.Middle;
    private static EmbeddedDocumentTextColor Foreground(string style) => Color(style, "color") switch
    {
        EmbeddedSheetCellColor.Red => EmbeddedDocumentTextColor.Red,
        EmbeddedSheetCellColor.Orange => EmbeddedDocumentTextColor.Orange,
        EmbeddedSheetCellColor.Yellow => EmbeddedDocumentTextColor.Yellow,
        EmbeddedSheetCellColor.Green => EmbeddedDocumentTextColor.Green,
        EmbeddedSheetCellColor.Teal => EmbeddedDocumentTextColor.Teal,
        EmbeddedSheetCellColor.Blue => EmbeddedDocumentTextColor.Blue,
        EmbeddedSheetCellColor.Purple => EmbeddedDocumentTextColor.Purple,
        EmbeddedSheetCellColor.Pink => EmbeddedDocumentTextColor.Pink,
        EmbeddedSheetCellColor.Gray => EmbeddedDocumentTextColor.Gray,
        _ => EmbeddedDocumentTextColor.Default
    };
    private static EmbeddedSheetCellColor Background(string style) => Color(style, "background(?:-color)?");
    private static EmbeddedSheetFontSize FontSize(string style)
    {
        var match = Regex.Match(style, @"(?:^|;)\s*font-size\s*:\s*(\d+(?:\.\d+)?)(px|pt)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var size))
            return EmbeddedSheetFontSize.Normal;
        if (match.Groups[2].Value.Equals("pt", StringComparison.OrdinalIgnoreCase)) size *= 96d / 72d;
        return size switch { <= 11 => EmbeddedSheetFontSize.Small, <= 14 => EmbeddedSheetFontSize.Normal,
            <= 18 => EmbeddedSheetFontSize.Medium, <= 24 => EmbeddedSheetFontSize.Large, _ => EmbeddedSheetFontSize.Heading };
    }
    private static double? FontSizePixels(string style)
    {
        var match = Regex.Match(style, @"(?:^|;)\s*font-size\s*:\s*(\d+(?:\.\d+)?)(px|pt)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var size)) return null;
        return Math.Clamp(match.Groups[2].Value.Equals("pt", StringComparison.OrdinalIgnoreCase) ? size * 96d / 72d : size, 8, 48);
    }
    private static string? FontFamily(string style)
    {
        var match = Regex.Match(style, @"(?:^|;)\s*font-family\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : null;
    }
    private static int? FontWeight(string style)
    {
        var match = Regex.Match(style, @"(?:^|;)\s*font-weight\s*:\s*(bold|normal|[1-9]00)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups[1].Value.ToLowerInvariant() switch { "bold" => 700, "normal" => 400,
            var value when int.TryParse(value, out var parsed) => parsed, _ => null };
    }
    private static bool? WrapText(string style) => Regex.IsMatch(style, @"white-space\s*:\s*(pre-wrap|normal)")
        ? true : Regex.IsMatch(style, @"white-space\s*:\s*nowrap") ? false : null;
    private static EmbeddedSheetCellColor Color(string style, string property)
    {
        var match = Regex.Match(style, $@"(?:^|;)\s*{property}\s*:\s*(#[0-9a-f]{{3,6}}|rgb\([^)]*\))",
            RegexOptions.IgnoreCase);
        if (!match.Success || !TryRgb(match.Groups[1].Value, out var r, out var g, out var b)) return EmbeddedSheetCellColor.Default;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        if (min > 235) return EmbeddedSheetCellColor.Light;
        if (max < 55) return EmbeddedSheetCellColor.Dark;
        if (max - min < 25) return EmbeddedSheetCellColor.Gray;
        if (r > 180 && g < 145) return g > 75 ? EmbeddedSheetCellColor.Orange : EmbeddedSheetCellColor.Red;
        if (r > 180 && g > 160 && b < 135) return EmbeddedSheetCellColor.Yellow;
        if (g > r * 1.15 && g > b * 1.05) return EmbeddedSheetCellColor.Green;
        if (g > r && b > r && Math.Abs(g - b) < 80) return EmbeddedSheetCellColor.Teal;
        if (b > r * 1.15) return r > 120 ? EmbeddedSheetCellColor.Purple : EmbeddedSheetCellColor.Blue;
        return r > b * 1.25 ? EmbeddedSheetCellColor.Pink : EmbeddedSheetCellColor.Gray;
    }
    private static bool TryRgb(string value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (value[0] == '#')
        {
            var hex = value[1..]; if (hex.Length == 3) hex = string.Concat(hex.Select(character => $"{character}{character}"));
            if (hex.Length == 8) hex = hex[..6];
            return hex.Length == 6 && int.TryParse(hex[..2], NumberStyles.HexNumber, null, out r) &&
                int.TryParse(hex[2..4], NumberStyles.HexNumber, null, out g) && int.TryParse(hex[4..], NumberStyles.HexNumber, null, out b);
        }
        var matches = Regex.Matches(value, @"\d+").Take(3).ToArray();
        if (matches.Length != 3 || !int.TryParse(matches[0].Value, out r) ||
            !int.TryParse(matches[1].Value, out g) || !int.TryParse(matches[2].Value, out b)) return false;
        r = Math.Clamp(r, 0, 255); g = Math.Clamp(g, 0, 255); b = Math.Clamp(b, 0, 255); return true;
    }
    private static EmbeddedSheetBorderStyle Border(string style, string side)
    {
        var match = Regex.Match(style, $@"border-{side}\s*:\s*([^;]+)");
        if (!match.Success || match.Groups[1].Value.Contains("none")) return EmbeddedSheetBorderStyle.None;
        var value = match.Groups[1].Value;
        if (value.Contains("dashed")) return EmbeddedSheetBorderStyle.Dashed;
        if (value.Contains("dotted")) return EmbeddedSheetBorderStyle.Dotted;
        if (Regex.IsMatch(value, @"(?:3|4|5|6|7|8|9)px")) return EmbeddedSheetBorderStyle.Thick;
        if (value.Contains("2px")) return EmbeddedSheetBorderStyle.Medium;
        return EmbeddedSheetBorderStyle.Thin;
    }
    private static string? BorderColor(string style, string side)
    {
        var match = Regex.Match(style, $@"border-{side}\s*:\s*([^;]+)");
        return match.Success ? NormalizeColor(Regex.Match(match.Groups[1].Value,
            @"#[0-9a-f]{3,8}|rgb\([^)]*\)", RegexOptions.IgnoreCase).Value) : null;
    }
    private static string? HexColor(string style, string property)
    {
        var match = Regex.Match(style, $@"(?:^|;)\s*{property}\s*:\s*(#[0-9a-f]{{3,8}}|rgb\([^)]*\))",
            RegexOptions.IgnoreCase);
        return match.Success ? NormalizeColor(match.Groups[1].Value) : null;
    }
    private static string? NormalizeColor(string value)
    {
        if (string.IsNullOrEmpty(value) || !TryRgb(value, out var r, out var g, out var b)) return null;
        return $"#{r:X2}{g:X2}{b:X2}";
    }
    private static string? SafeLink(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CssRule();
}

public sealed class GoogleSheetsTooLargeException : Exception;
