using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Iridium.Protocol;

namespace Iridium.Server.Embeds;

/// <summary>Reads the bounded, anonymous Google XLSX export into Iridium's provider-neutral sheet DTO.</summary>
public sealed class GoogleSheetsXlsxParser
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace SpreadsheetDrawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const double CssPixelsPerPoint = 96d / 72d;
    private const int GoogleDefaultColumnWidth = 100;
    private const int GoogleDefaultRowHeight = 21;

    public EmbeddedSheetDto? Parse(byte[] source, EmbeddedContentConfiguration configuration)
        => ParseWithMedia(source, configuration)?.Sheet;

    public GoogleSheetsXlsxParseResult? ParseWithMedia(byte[] source, EmbeddedContentConfiguration configuration)
    {
        using var input = new MemoryStream(source, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var workbook = Xml(archive, "xl/workbook.xml");
        var relations = Xml(archive, "xl/_rels/workbook.xml.rels");
        if (workbook is null || relations is null) return null;

        var targets = relations.Root?.Elements(PackageRelationships + "Relationship")
            .Where(value => value.Attribute("Id") is not null && value.Attribute("Target") is not null)
            .ToDictionary(value => (string)value.Attribute("Id")!, value => NormalizeTarget((string)value.Attribute("Target")!),
                StringComparer.Ordinal) ?? [];
        var strings = SharedStrings(archive);
        var themeTarget = relations.Root?.Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(value => ((string?)value.Attribute("Type"))?.EndsWith("/theme",
                StringComparison.OrdinalIgnoreCase) == true)?.Attribute("Target")?.Value;
        var theme = Xml(archive, themeTarget is null ? "xl/theme/theme1.xml" : NormalizeTarget(themeTarget));
        var styles = WorkbookStyles.Read(Xml(archive, "xl/styles.xml"), theme);
        var tabs = new List<EmbeddedSheetTabDto>();
        var media = new Dictionary<string, EmbeddedDocumentMedia>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Descendants(Main + "sheet").Take(GoogleSheetsHtmlParser.MaximumTabs))
        {
            var relationId = (string?)sheet.Attribute(Relationships + "id");
            if (relationId is null || !targets.TryGetValue(relationId, out var target) || Xml(archive, target) is not { } worksheet)
                continue;
            var id = (string?)sheet.Attribute("sheetId") ?? tabs.Count.ToString(CultureInfo.InvariantCulture);
            var name = (string?)sheet.Attribute("name") ?? $"Sheet {tabs.Count + 1}";
            tabs.Add(ParseSheet(archive, target, worksheet, id, name, strings, styles, media));
        }
        if (tabs.Count == 0) return null;
        var requested = configuration.TabId;
        var defaultId = requested is not null && tabs.Any(tab => tab.Id == requested) ? requested : tabs[0].Id;
        return new(new(configuration.SourceId, null, tabs, defaultId), media);
    }

    private static EmbeddedSheetTabDto ParseSheet(ZipArchive archive, string path, XDocument worksheet,
        string id, string name, IReadOnlyList<string> strings, WorkbookStyles styles,
        Dictionary<string, EmbeddedDocumentMedia> media)
    {
        var raw = new Dictionary<(int Row, int Column), RawCell>();
        var rowHeights = new Dictionary<int, int>();
        var rowStyles = new Dictionary<int, int>();
        var sheetFormat = worksheet.Root?.Element(Main + "sheetFormatPr");
        var defaultRowHeight = AttributeDouble(sheetFormat, "defaultRowHeight") is { } defaultHeight
            ? PointHeight(defaultHeight) : GoogleDefaultRowHeight;
        var defaultColumnWidth = AttributeDouble(sheetFormat, "defaultColWidth") is { } defaultWidth
            ? ExcelWidth(defaultWidth) : GoogleDefaultColumnWidth;
        var maxRow = -1;
        var maxColumn = -1;
        foreach (var row in worksheet.Descendants(Main + "row"))
        {
            var rowIndex = Math.Max(0, AttributeInt(row, "r", raw.Count + 1) - 1);
            if (rowIndex >= GoogleSheetsHtmlParser.MaximumRows) throw new GoogleSheetsTooLargeException();
            maxRow = Math.Max(maxRow, rowIndex);
            if (AttributeDouble(row, "ht") is { } height)
                rowHeights[rowIndex] = PointHeight(height);
            if (AttributeInt(row, "s", -1) is >= 0 and var rowStyle) rowStyles[rowIndex] = rowStyle;
            var inferredColumn = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var coordinate = reference is null ? (Row: rowIndex, Column: inferredColumn) : Coordinate(reference);
                inferredColumn = coordinate.Column + 1;
                if (coordinate.Column >= GoogleSheetsHtmlParser.MaximumColumns) throw new GoogleSheetsTooLargeException();
                maxColumn = Math.Max(maxColumn, coordinate.Column);
                var styleValue = AttributeInt(cell, "s", -1);
                int? explicitStyle = styleValue >= 0 ? styleValue : null;
                var effectiveStyle = explicitStyle ?? rowStyles.GetValueOrDefault(rowIndex);
                var parsed = CellValue(cell, strings, styles.Format(effectiveStyle));
                raw[coordinate] = new(parsed.Display, parsed.Raw, explicitStyle, parsed.IsNumeric);
                if (raw.Count > GoogleSheetsHtmlParser.MaximumCells) throw new GoogleSheetsTooLargeException();
            }
        }

        var merges = new Dictionary<(int Row, int Column), (int Rows, int Columns)>();
        var covered = new HashSet<(int Row, int Column)>();
        foreach (var merge in worksheet.Descendants(Main + "mergeCell"))
        {
            var references = ((string?)merge.Attribute("ref"))?.Split(':', 2);
            if (references is not { Length: 2 }) continue;
            var start = Coordinate(references[0]); var end = Coordinate(references[1]);
            if (start.Row > end.Row || start.Column > end.Column || end.Row >= GoogleSheetsHtmlParser.MaximumRows ||
                end.Column >= GoogleSheetsHtmlParser.MaximumColumns) continue;
            var span = (end.Row - start.Row + 1, end.Column - start.Column + 1);
            merges[start] = span;
            for (var row = start.Row; row <= end.Row; row++)
                for (var column = start.Column; column <= end.Column; column++)
                    if ((row, column) != start) covered.Add((row, column));
            maxRow = Math.Max(maxRow, end.Row); maxColumn = Math.Max(maxColumn, end.Column);
        }

        var columnStyles = new Dictionary<int, int>();
        var columnWidths = new Dictionary<int, int>();
        foreach (var column in worksheet.Descendants(Main + "col"))
        {
            var first = Math.Max(0, AttributeInt(column, "min", 1) - 1);
            var last = Math.Min(GoogleSheetsHtmlParser.MaximumColumns - 1, AttributeInt(column, "max", first + 1) - 1);
            var width = AttributeDouble(column, "width") is { } value ? ExcelWidth(value) : defaultColumnWidth;
            var style = AttributeInt(column, "style", -1);
            for (var index = first; index <= last; index++)
            {
                columnWidths[index] = width;
                if (style >= 0) columnStyles[index] = style;
            }
            // Explicitly sized spacer columns near the used cells are meaningful; avoid whole-sheet formatting explosions.
            if (first <= maxColumn + 64) maxColumn = Math.Max(maxColumn, Math.Min(last, maxColumn + 64));
        }
        if (maxRow < 0 || maxColumn < 0 || (long)(maxRow + 1) * (maxColumn + 1) > GoogleSheetsHtmlParser.MaximumCells)
            throw new GoogleSheetsTooLargeException();

        var hyperlinks = Hyperlinks(archive, path, worksheet);
        var rows = new List<EmbeddedSheetRowDto>(maxRow + 1);
        for (var rowIndex = 0; rowIndex <= maxRow; rowIndex++)
        {
            var cells = new List<EmbeddedSheetCellDto>(maxColumn + 1);
            for (var columnIndex = 0; columnIndex <= maxColumn; columnIndex++)
            {
                if (covered.Contains((rowIndex, columnIndex))) continue;
                raw.TryGetValue((rowIndex, columnIndex), out var value);
                var styleIndex = value.Style ?? (rowStyles.TryGetValue(rowIndex, out var rowStyle)
                    ? rowStyle : columnStyles.GetValueOrDefault(columnIndex));
                var style = styles.Cell(styleIndex);
                var span = merges.GetValueOrDefault((rowIndex, columnIndex), (Rows: 1, Columns: 1));
                var perimeter = span == (1, 1) ? new WorkbookStyles.BorderSet(style.Top, style.Right, style.Bottom, style.Left) :
                    MergedPerimeter(rowIndex, columnIndex, span, raw, rowStyles, columnStyles, styles);
                var checkbox = string.Equals(value.Value, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(value.Value, "FALSE", StringComparison.OrdinalIgnoreCase);
                bool? checkedValue = checkbox ? string.Equals(value.Value, "TRUE", StringComparison.OrdinalIgnoreCase) : null;
                var horizontal = style.Horizontal ?? (checkbox ? EmbeddedDocumentTextAlignment.Center :
                    value.IsNumeric ? EmbeddedDocumentTextAlignment.End : EmbeddedDocumentTextAlignment.Start);
                cells.Add(new(rowIndex, columnIndex, checkbox ? checkedValue == true ? "☑" : "☐" : value.Value ?? string.Empty,
                    span.Rows, span.Columns, style.Bold, style.Italic, style.Underline, horizontal, style.Vertical,
                    EmbeddedDocumentTextColor.Default, EmbeddedSheetCellColor.Default, perimeter.Top.Style, perimeter.Right.Style,
                    perimeter.Bottom.Style, perimeter.Left.Style, hyperlinks.GetValueOrDefault((rowIndex, columnIndex)), checkbox,
                    checkedValue, style.FontSize, style.Foreground, style.Background, perimeter.Top.Color, perimeter.Right.Color,
                    perimeter.Bottom.Color, perimeter.Left.Color, value.RawValue, style.FontFamily, style.FontSizePx,
                    style.FontWeight, style.WrapText, style.IndentLevel, styleIndex));
            }
            rows.Add(new(rowHeights.GetValueOrDefault(rowIndex, defaultRowHeight), cells, rowIndex));
        }
        var widths = Enumerable.Range(0, maxColumn + 1)
            .Select(index => columnWidths.GetValueOrDefault(index, defaultColumnWidth)).ToArray();
        var showGridLines = !string.Equals((string?)worksheet.Descendants(Main + "sheetView").FirstOrDefault()?
            .Attribute("showGridLines"), "0", StringComparison.OrdinalIgnoreCase);
        return new(id, name, rows, widths, Images(archive, path, worksheet, media, widths,
            rows.Select(row => row.Height ?? defaultRowHeight).ToArray()), showGridLines);
    }

    private static WorkbookStyles.BorderSet MergedPerimeter(int row, int column, (int Rows, int Columns) span,
        IReadOnlyDictionary<(int Row, int Column), RawCell> raw, IReadOnlyDictionary<int, int> rowStyles,
        IReadOnlyDictionary<int, int> columnStyles, WorkbookStyles styles)
    {
        WorkbookStyles.CellStyle Cell(int targetRow, int targetColumn)
        {
            raw.TryGetValue((targetRow, targetColumn), out var value);
            var styleIndex = value.Style ?? (rowStyles.TryGetValue(targetRow, out var rowStyle)
                ? rowStyle : columnStyles.GetValueOrDefault(targetColumn));
            return styles.Cell(styleIndex);
        }

        var lastRow = row + span.Rows - 1;
        var lastColumn = column + span.Columns - 1;
        return new(
            Strongest(Enumerable.Range(column, span.Columns).Select(target => Cell(row, target).Top)),
            Strongest(Enumerable.Range(row, span.Rows).Select(target => Cell(target, lastColumn).Right)),
            Strongest(Enumerable.Range(column, span.Columns).Select(target => Cell(lastRow, target).Bottom)),
            Strongest(Enumerable.Range(row, span.Rows).Select(target => Cell(target, column).Left)));
    }

    private static WorkbookStyles.BorderEdge Strongest(IEnumerable<WorkbookStyles.BorderEdge> edges) =>
        edges.OrderByDescending(edge => edge.Style switch
        {
            EmbeddedSheetBorderStyle.Double => 6,
            EmbeddedSheetBorderStyle.Thick => 5,
            EmbeddedSheetBorderStyle.Medium => 4,
            EmbeddedSheetBorderStyle.Dashed => 3,
            EmbeddedSheetBorderStyle.Dotted => 2,
            EmbeddedSheetBorderStyle.Thin => 1,
            _ => 0
        }).FirstOrDefault() ?? WorkbookStyles.BorderEdge.Default;

    private static IReadOnlyList<EmbeddedSheetImageDto> Images(ZipArchive archive, string sheetPath,
        XDocument worksheet, Dictionary<string, EmbeddedDocumentMedia> media,
        IReadOnlyList<int> columnWidths, IReadOnlyList<int> rowHeights)
    {
        var result = new List<EmbeddedSheetImageDto>();
        var sheetRelations = PartRelationships(archive, sheetPath);
        foreach (var drawingReference in worksheet.Descendants(Main + "drawing"))
        {
            var relationId = (string?)drawingReference.Attribute(Relationships + "id");
            if (relationId is null || !sheetRelations.TryGetValue(relationId, out var drawingPath) ||
                Xml(archive, drawingPath) is not { } drawing) continue;
            var drawingRelations = PartRelationships(archive, drawingPath);
            foreach (var anchor in drawing.Root?.Elements() ?? [])
            {
                var from = anchor.Element(SpreadsheetDrawing + "from");
                var imageRelation = (string?)anchor.Descendants(Drawing + "blip").FirstOrDefault()?
                    .Attribute(Relationships + "embed");
                if (from is null || imageRelation is null || !drawingRelations.TryGetValue(imageRelation, out var imagePath) ||
                    archive.GetEntry(imagePath) is not { } entry || entry.Length is <= 0 or > 12 * 1024 * 1024) continue;
                using var stream = entry.Open(); using var destination = new MemoryStream(); stream.CopyTo(destination);
                var bytes = destination.ToArray(); var contentType = ImageContentType(bytes);
                if (contentType is null) continue;
                var mediaId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..32];
                media.TryAdd(mediaId, new(bytes, contentType));
                var extent = anchor.Element(SpreadsheetDrawing + "ext");
                var to = anchor.Element(SpreadsheetDrawing + "to");
                var fromColumn = CoordinatePart(from, "col");
                var fromRow = CoordinatePart(from, "row");
                var offsetX = Emu(LongPart(from, "colOff")) ?? 0;
                var offsetY = Emu(LongPart(from, "rowOff")) ?? 0;
                var width = Emu((long?)extent?.Attribute("cx")) ?? AnchorSpan(columnWidths, fromColumn,
                    CoordinatePart(to, "col"), offsetX, Emu(LongPart(to, "colOff")) ?? 0);
                var height = Emu((long?)extent?.Attribute("cy")) ?? AnchorSpan(rowHeights, fromRow,
                    CoordinatePart(to, "row"), offsetY, Emu(LongPart(to, "rowOff")) ?? 0);
                result.Add(new(mediaId, fromRow, fromColumn, offsetX, offsetY,
                    Math.Clamp(width, 16, 2400), Math.Clamp(height, 16, 2400),
                    (string?)anchor.Descendants(SpreadsheetDrawing + "cNvPr").FirstOrDefault()?.Attribute("descr")));
            }
        }
        return result;
    }

    private static int AnchorSpan(IReadOnlyList<int> sizes, int from, int to, int fromOffset, int toOffset)
    {
        var start = Math.Clamp(from, 0, sizes.Count);
        var end = Math.Clamp(to, start, sizes.Count);
        return Math.Max(1, sizes.Skip(start).Take(end - start).Sum() + toOffset - fromOffset);
    }

    private static Dictionary<string, string> PartRelationships(ZipArchive archive, string partPath)
    {
        var directory = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty;
        var relationships = Xml(archive, $"{directory}/_rels/{Path.GetFileName(partPath)}.rels");
        return relationships?.Root?.Elements(PackageRelationships + "Relationship")
            .Where(value => value.Attribute("Id") is not null && value.Attribute("Target") is not null)
            .ToDictionary(value => (string)value.Attribute("Id")!, value => ResolvePart(partPath,
                (string)value.Attribute("Target")!), StringComparer.Ordinal) ?? [];
    }
    private static string ResolvePart(string basePart, string target)
    {
        var stack = new List<string>();
        foreach (var segment in $"{Path.GetDirectoryName(basePart)?.Replace('\\', '/')}/{target}".Split('/'))
            if (segment == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else if (segment is not ("" or ".")) stack.Add(segment);
        return string.Join('/', stack);
    }
    private static int CoordinatePart(XElement? value, string name) =>
        int.TryParse(value?.Element(SpreadsheetDrawing + name)?.Value, out var parsed) ? parsed : 0;
    private static long? LongPart(XElement? value, string name) =>
        long.TryParse(value?.Element(SpreadsheetDrawing + name)?.Value, out var parsed) ? parsed : null;
    private static int? Emu(long? value) => value is null ? null : (int)Math.Round(value.Value / 9525d);
    private static string? ImageContentType(byte[] value) =>
        value.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) ? "image/png" :
        value.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ? "image/jpeg" :
        value.AsSpan().StartsWith("GIF8"u8) ? "image/gif" :
        value.Length >= 12 && value.AsSpan(0, 4).SequenceEqual("RIFF"u8) && value.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            ? "image/webp" : null;

    private static Dictionary<(int Row, int Column), string> Hyperlinks(ZipArchive archive, string sheetPath,
        XDocument worksheet)
    {
        var result = new Dictionary<(int, int), string>();
        var file = Path.GetFileName(sheetPath); var directory = Path.GetDirectoryName(sheetPath)?.Replace('\\', '/') ?? "xl/worksheets";
        var relationships = Xml(archive, $"{directory}/_rels/{file}.rels");
        var targets = relationships?.Root?.Elements(PackageRelationships + "Relationship")
            .Where(value => value.Attribute("Id") is not null && SafeLink((string?)value.Attribute("Target")) is not null)
            .ToDictionary(value => (string)value.Attribute("Id")!, value => SafeLink((string?)value.Attribute("Target"))!, StringComparer.Ordinal) ?? [];
        foreach (var link in worksheet.Descendants(Main + "hyperlink"))
        {
            var reference = (string?)link.Attribute("ref"); var relation = (string?)link.Attribute(Relationships + "id");
            if (reference is not null && relation is not null && targets.TryGetValue(relation, out var target)) result[Coordinate(reference)] = target;
        }
        return result;
    }

    private static (string Display, string? Raw, bool IsNumeric) CellValue(XElement cell,
        IReadOnlyList<string> strings, string? format)
    {
        var type = (string?)cell.Attribute("t");
        var raw = (string?)cell.Element(Main + "v") ?? string.Concat(cell.Descendants(Main + "t").Select(value => value.Value));
        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < strings.Count)
            return (strings[index], raw, false);
        if (type == "b") return (raw == "1" ? "TRUE" : "FALSE", raw, false);
        if (type is "str" or "inlineStr" || string.IsNullOrEmpty(raw)) return (raw ?? string.Empty, raw, false);
        return (FormatNumber(raw, format), raw, double.TryParse(raw, NumberStyles.Float,
            CultureInfo.InvariantCulture, out _));
    }

    private static string FormatNumber(string value, string? format)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;
        if (string.IsNullOrWhiteSpace(format) || string.Equals(format, "General", StringComparison.OrdinalIgnoreCase))
            return Math.Abs(number % 1) < .0000001 ? number.ToString("0", CultureInfo.InvariantCulture) :
                number.ToString("0.########", CultureInfo.InvariantCulture);
        if (format.StartsWith("date:", StringComparison.Ordinal))
        {
            try { return DateTime.FromOADate(number).ToString(format[5..], CultureInfo.InvariantCulture); }
            catch (ArgumentException) { return value; }
        }
        try
        {
            // Excel and .NET share the common numeric placeholders used by Sheets exports, including
            // zero-decimal rounding, optional decimals, grouping, percentages, literals, and sections.
            return number.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return number.ToString("0.########", CultureInfo.InvariantCulture);
        }
    }

    private static IReadOnlyList<string> SharedStrings(ZipArchive archive) => Xml(archive, "xl/sharedStrings.xml")?
        .Descendants(Main + "si").Select(value => string.Concat(value.Descendants(Main + "t").Select(text => text.Value))).ToArray() ?? [];
    private static XDocument? Xml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path.Replace('\\', '/')); if (entry is null) return null;
        if (entry.Length > GoogleSheetsPublishedService.MaximumResponseBytes) throw new GoogleSheetsTooLargeException();
        using var stream = entry.Open(); return XDocument.Load(stream, LoadOptions.None);
    }
    private static string NormalizeTarget(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("xl/", StringComparison.Ordinal) ? normalized : $"xl/{normalized.TrimStart('.', '/')}";
    }
    private static (int Row, int Column) Coordinate(string reference)
    {
        var letters = reference.TakeWhile(char.IsLetter).ToArray();
        var column = 0; foreach (var letter in letters) column = checked(column * 26 + char.ToUpperInvariant(letter) - 'A' + 1);
        _ = int.TryParse(reference[letters.Length..].TrimEnd('$'), out var row);
        return (Math.Max(0, row - 1), Math.Max(0, column - 1));
    }
    private static int AttributeInt(XElement value, string name, int fallback) =>
        int.TryParse((string?)value.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static double? AttributeDouble(XElement? value, string name) =>
        double.TryParse((string?)value?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static int PointHeight(double points) => Math.Clamp((int)Math.Round(points * CssPixelsPerPoint), 1, 1000);
    private static int ExcelWidth(double width)
    {
        const double maximumDigitWidth = 7d;
        var pixels = width < 1d
            ? (int)Math.Floor(width * (maximumDigitWidth + 5d) + .5d)
            : (int)Math.Floor(((256d * width + Math.Floor(128d / maximumDigitWidth)) / 256d) * maximumDigitWidth) + 5;
        return Math.Clamp(pixels, 1, 2000);
    }
    private static string? SafeLink(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
    private readonly record struct RawCell(string? Value, string? RawValue, int? Style, bool IsNumeric);

    private sealed class WorkbookStyles
    {
        private readonly CellStyle[] _cells;
        private readonly string?[] _formats;
        private WorkbookStyles(CellStyle[] cells, string?[] formats) { _cells = cells; _formats = formats; }
        public CellStyle Cell(int index) => index >= 0 && index < _cells.Length ? _cells[index] : CellStyle.Default;
        public string? Format(int index) => Cell(index).NumberFormatId is var id && id >= 0 && id < _formats.Length ? _formats[id] : null;

        public static WorkbookStyles Read(XDocument? document, XDocument? theme)
        {
            if (document?.Root is null) return new([CellStyle.Default], []);
            var themeColors = ThemeColors(theme);
            var indexedColors = IndexedColors(document);
            var fonts = document.Root.Element(Main + "fonts")?.Elements(Main + "font")
                .Select(value => Font(value, themeColors, indexedColors)).ToArray() ?? [FontStyle.Default];
            var fills = document.Root.Element(Main + "fills")?.Elements(Main + "fill")
                .Select(value => Fill(value, themeColors, indexedColors)).ToArray() ?? ["#FFFFFF"];
            var borders = document.Root.Element(Main + "borders")?.Elements(Main + "border").Select(Border).ToArray() ?? [BorderSet.Default];
            var formats = BuiltInFormats();
            foreach (var format in document.Descendants(Main + "numFmt"))
                if (AttributeInt(format, "numFmtId", -1) is >= 0 and < 512 and var id) formats[id] = (string?)format.Attribute("formatCode");
            var cells = document.Root.Element(Main + "cellXfs")?.Elements(Main + "xf").Select(value =>
            {
                var font = fonts.ElementAtOrDefault(AttributeInt(value, "fontId", 0)) ?? FontStyle.Default;
                var background = fills.ElementAtOrDefault(AttributeInt(value, "fillId", 0));
                var border = borders.ElementAtOrDefault(AttributeInt(value, "borderId", 0)) ?? BorderSet.Default;
                var alignment = value.Element(Main + "alignment");
                return new CellStyle(font.Bold, font.Italic, font.Underline, FontScale(font.Size), font.Color,
                    background, Horizontal(alignment), Vertical(alignment), border.Top, border.Right, border.Bottom,
                    border.Left, AttributeInt(value, "numFmtId", 0), font.Family, font.Size * CssPixelsPerPoint,
                    font.Bold ? 700 : 400, (bool?)alignment?.Attribute("wrapText"),
                    Math.Clamp(AttributeInt(alignment ?? new XElement("alignment"), "indent", 0), 0, 15));
            }).ToArray() ?? [CellStyle.Default];
            return new(cells, formats);
        }

        private static string?[] BuiltInFormats()
        {
            var formats = new string?[512];
            formats[1] = "0"; formats[2] = "0.00"; formats[3] = "#,##0"; formats[4] = "#,##0.00";
            formats[9] = "0%"; formats[10] = "0.00%"; formats[11] = "0.00E+00";
            formats[14] = "date:MM-dd-yy"; formats[15] = "date:d-MMM-yy";
            formats[16] = "date:d-MMM"; formats[17] = "date:MMM-yy";
            formats[18] = "date:h:mm tt"; formats[19] = "date:h:mm:ss tt";
            formats[20] = "date:H:mm"; formats[21] = "date:H:mm:ss"; formats[22] = "date:MM-dd-yy H:mm";
            formats[37] = "#,##0;(#,##0)"; formats[38] = "#,##0;(#,##0)";
            formats[39] = "#,##0.00;(#,##0.00)"; formats[40] = "#,##0.00;(#,##0.00)";
            return formats;
        }

        private static FontStyle Font(XElement value, IReadOnlyDictionary<int, string> themeColors,
            IReadOnlyList<string?> indexedColors) => new(BooleanElement(value.Element(Main + "b")),
            BooleanElement(value.Element(Main + "i")), BooleanElement(value.Element(Main + "u")),
            AttributeDouble(value.Element(Main + "sz") ?? new XElement("none"), "val") ?? 10,
            Color(value.Element(Main + "color"), themeColors, indexedColors),
            (string?)value.Element(Main + "name")?.Attribute("val"));
        private static string? Fill(XElement value, IReadOnlyDictionary<int, string> themeColors,
            IReadOnlyList<string?> indexedColors)
        {
            var pattern = value.Element(Main + "patternFill");
            return (string?)pattern?.Attribute("patternType") == "solid"
                ? Color(pattern.Element(Main + "fgColor"), themeColors, indexedColors)
                : "#FFFFFF";
        }

        private static IReadOnlyDictionary<int, string> ThemeColors(XDocument? theme)
        {
            var result = new Dictionary<int, string>();
            var scheme = theme?.Descendants(Drawing + "clrScheme").FirstOrDefault();
            if (scheme is null) return result;
            var index = 0;
            foreach (var slot in scheme.Elements())
            {
                var definition = slot.Elements().FirstOrDefault();
                var value = definition?.Name.LocalName == "sysClr"
                    ? (string?)definition.Attribute("lastClr")
                    : (string?)definition?.Attribute("val");
                if (NormalizeRgb(value) is { } color) result[index] = color;
                index++;
            }
            return result;
        }

        private static IReadOnlyList<string?> IndexedColors(XDocument styles)
        {
            var result = StandardIndexedColors.ToArray();
            var custom = styles.Root?.Element(Main + "colors")?.Element(Main + "indexedColors")?
                .Elements(Main + "rgbColor").ToArray();
            if (custom is null) return result;
            for (var index = 0; index < custom.Length && index < result.Length; index++)
                result[index] = NormalizeRgb((string?)custom[index].Attribute("rgb"));
            return result;
        }

        private static string? Color(XElement? value, IReadOnlyDictionary<int, string> themeColors,
            IReadOnlyList<string?> indexedColors)
        {
            if (NormalizeRgb((string?)value?.Attribute("rgb")) is { } rgb) return rgb;
            if (AttributeInt(value ?? new XElement("color"), "theme", -1) is >= 0 and var themeIndex &&
                themeColors.TryGetValue(themeIndex, out var themed))
                return ApplyTint(themed, AttributeDouble(value, "tint") ?? 0);
            var indexed = AttributeInt(value ?? new XElement("color"), "indexed", -1);
            return indexed >= 0 && indexed < indexedColors.Count ? indexedColors[indexed] : null;
        }

        private static string? NormalizeRgb(string? value)
        {
            var rgb = value?.Trim().TrimStart('#');
            if (rgb is { Length: 8 }) rgb = rgb[2..];
            return rgb is { Length: 6 } && rgb.All(Uri.IsHexDigit) ? $"#{rgb.ToUpperInvariant()}" : null;
        }

        private static string ApplyTint(string source, double tint)
        {
            tint = Math.Clamp(tint, -1, 1);
            var r = int.Parse(source.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            var g = int.Parse(source.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            var b = int.Parse(source.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var lightness = (max + min) / 2;
            var delta = max - min;
            var saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
            var hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) :
                max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;
            lightness = tint < 0 ? lightness * (1 + tint) : lightness + (1 - lightness) * tint;
            static double Channel(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                return t < 1d / 6 ? p + (q - p) * 6 * t : t < .5 ? q :
                    t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
            }
            var h = hue / 360;
            var q = lightness < .5 ? lightness * (1 + saturation) :
                lightness + saturation - lightness * saturation;
            var p = 2 * lightness - q;
            var red = saturation == 0 ? lightness : Channel(p, q, h + 1d / 3);
            var green = saturation == 0 ? lightness : Channel(p, q, h);
            var blue = saturation == 0 ? lightness : Channel(p, q, h - 1d / 3);
            static int Byte(double channel) => Math.Clamp((int)Math.Round(channel * 255), 0, 255);
            return $"#{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}";
        }
        private static BorderSet Border(XElement value) => new(Edge(value.Element(Main + "top")),
            Edge(value.Element(Main + "right")), Edge(value.Element(Main + "bottom")), Edge(value.Element(Main + "left")));
        private static BorderEdge Edge(XElement? value)
        {
            var style = (string?)value?.Attribute("style") switch
            {
                null or "none" => EmbeddedSheetBorderStyle.None,
                "medium" or "mediumDashed" => EmbeddedSheetBorderStyle.Medium,
                "thick" => EmbeddedSheetBorderStyle.Thick,
                "double" => EmbeddedSheetBorderStyle.Double,
                "dashed" or "dashDot" or "dashDotDot" => EmbeddedSheetBorderStyle.Dashed,
                "dotted" => EmbeddedSheetBorderStyle.Dotted,
                _ => EmbeddedSheetBorderStyle.Thin
            };
            return new(style, style == EmbeddedSheetBorderStyle.None ? null :
                Rgb(value?.Element(Main + "color")) ?? "#000000");
        }
        private static EmbeddedDocumentTextAlignment? Horizontal(XElement? value) => (string?)value?.Attribute("horizontal") switch
        { "center" or "centerContinuous" => EmbeddedDocumentTextAlignment.Center,
            "right" => EmbeddedDocumentTextAlignment.End, "left" => EmbeddedDocumentTextAlignment.Start,
            "justify" => EmbeddedDocumentTextAlignment.Justify, _ => null };
        private static EmbeddedSheetVerticalAlignment Vertical(XElement? value) => (string?)value?.Attribute("vertical") switch
        { "top" => EmbeddedSheetVerticalAlignment.Top, "center" => EmbeddedSheetVerticalAlignment.Middle,
            _ => EmbeddedSheetVerticalAlignment.Bottom };
        private static bool BooleanElement(XElement? value) => value is not null &&
            !string.Equals((string?)value.Attribute("val"), "0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals((string?)value.Attribute("val"), "false", StringComparison.OrdinalIgnoreCase);
        private static EmbeddedSheetFontSize FontScale(double size) => size switch
        { <= 9 => EmbeddedSheetFontSize.Small, <= 11 => EmbeddedSheetFontSize.Normal, <= 14 => EmbeddedSheetFontSize.Medium, <= 18 => EmbeddedSheetFontSize.Large, _ => EmbeddedSheetFontSize.Heading };
        private static string? Rgb(XElement? value)
        {
            return NormalizeRgb((string?)value?.Attribute("rgb"));
        }

        private static readonly string?[] StandardIndexedColors =
        [
            "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
            "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
            "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#C0C0C0", "#808080",
            "#9999FF", "#993366", "#FFFFCC", "#CCFFFF", "#660066", "#FF8080", "#0066CC", "#CCCCFF",
            "#000080", "#FF00FF", "#FFFF00", "#00FFFF", "#800080", "#800000", "#008080", "#0000FF",
            "#00CCFF", "#CCFFFF", "#CCFFCC", "#FFFF99", "#99CCFF", "#FF99CC", "#CC99FF", "#FFCC99",
            "#3366FF", "#33CCCC", "#99CC00", "#FFCC00", "#FF9900", "#FF6600", "#666699", "#969696",
            "#003366", "#339966", "#003300", "#333300", "#993300", "#993366", "#333399", "#333333"
        ];
        private sealed record FontStyle(bool Bold, bool Italic, bool Underline, double Size, string? Color,
            string? Family)
        { public static readonly FontStyle Default = new(false, false, false, 10, null, null); }
        public sealed record BorderSet(BorderEdge Top, BorderEdge Right, BorderEdge Bottom, BorderEdge Left)
        { public static readonly BorderSet Default = new(BorderEdge.Default, BorderEdge.Default, BorderEdge.Default, BorderEdge.Default); }
        public sealed record BorderEdge(EmbeddedSheetBorderStyle Style, string? Color)
        { public static readonly BorderEdge Default = new(EmbeddedSheetBorderStyle.None, null); }
        public sealed record CellStyle(bool Bold, bool Italic, bool Underline, EmbeddedSheetFontSize FontSize,
            string? Foreground, string? Background, EmbeddedDocumentTextAlignment? Horizontal,
            EmbeddedSheetVerticalAlignment Vertical, BorderEdge Top, BorderEdge Right, BorderEdge Bottom,
            BorderEdge Left, int NumberFormatId, string? FontFamily, double? FontSizePx, int? FontWeight,
            bool? WrapText, int IndentLevel)
        {
            public static readonly CellStyle Default = new(false, false, false, EmbeddedSheetFontSize.Normal,
                null, null, null, EmbeddedSheetVerticalAlignment.Bottom,
                BorderEdge.Default, BorderEdge.Default, BorderEdge.Default, BorderEdge.Default, 0, null, null, null,
                null, 0);
        }
    }
}

public sealed record GoogleSheetsXlsxParseResult(EmbeddedSheetDto Sheet,
    IReadOnlyDictionary<string, EmbeddedDocumentMedia> Media);
