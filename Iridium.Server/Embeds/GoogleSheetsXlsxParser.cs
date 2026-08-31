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
        var styles = WorkbookStyles.Read(Xml(archive, "xl/styles.xml"));
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
        var maxRow = -1;
        var maxColumn = -1;
        foreach (var row in worksheet.Descendants(Main + "row"))
        {
            var rowIndex = Math.Max(0, AttributeInt(row, "r", raw.Count + 1) - 1);
            if (rowIndex >= GoogleSheetsHtmlParser.MaximumRows) throw new GoogleSheetsTooLargeException();
            maxRow = Math.Max(maxRow, rowIndex);
            if (AttributeDouble(row, "ht") is { } height)
                rowHeights[rowIndex] = Math.Clamp((int)Math.Round(height * 96d / 72d), 12, 400);
            if (AttributeInt(row, "s", -1) is >= 0 and var rowStyle) rowStyles[rowIndex] = rowStyle;
            var inferredColumn = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var coordinate = reference is null ? (Row: rowIndex, Column: inferredColumn) : Coordinate(reference);
                inferredColumn = coordinate.Column + 1;
                if (coordinate.Column >= GoogleSheetsHtmlParser.MaximumColumns) throw new GoogleSheetsTooLargeException();
                maxColumn = Math.Max(maxColumn, coordinate.Column);
                var style = AttributeInt(cell, "s", rowStyles.GetValueOrDefault(rowIndex));
                var parsed = CellValue(cell, strings, styles.Format(style));
                raw[coordinate] = new(parsed.Display, parsed.Raw, style);
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
            var width = AttributeDouble(column, "width") is { } value ? ExcelWidth(value) : 100;
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
                var styleIndex = value.Style != 0 ? value.Style :
                    rowStyles.TryGetValue(rowIndex, out var rowStyle) ? rowStyle : columnStyles.GetValueOrDefault(columnIndex);
                var style = styles.Cell(styleIndex);
                var span = merges.GetValueOrDefault((rowIndex, columnIndex), (Rows: 1, Columns: 1));
                var checkbox = string.Equals(value.Value, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(value.Value, "FALSE", StringComparison.OrdinalIgnoreCase);
                bool? checkedValue = checkbox ? string.Equals(value.Value, "TRUE", StringComparison.OrdinalIgnoreCase) : null;
                cells.Add(new(rowIndex, columnIndex, checkbox ? checkedValue == true ? "☑" : "☐" : value.Value ?? string.Empty,
                    span.Rows, span.Columns, style.Bold, style.Italic, style.Underline, style.Horizontal, style.Vertical,
                    EmbeddedDocumentTextColor.Default, EmbeddedSheetCellColor.Default, style.Top.Style, style.Right.Style,
                    style.Bottom.Style, style.Left.Style, hyperlinks.GetValueOrDefault((rowIndex, columnIndex)), checkbox,
                    checkedValue, style.FontSize, style.Foreground, style.Background, style.Top.Color, style.Right.Color,
                    style.Bottom.Color, style.Left.Color, value.RawValue, style.FontFamily, style.FontSizePx,
                    style.FontWeight, style.WrapText));
            }
            rows.Add(new(rowHeights.GetValueOrDefault(rowIndex, 21), cells, rowIndex));
        }
        var widths = Enumerable.Range(0, maxColumn + 1).Select(index => columnWidths.GetValueOrDefault(index, 100)).ToArray();
        return new(id, name, rows, widths, Images(archive, path, worksheet, media));
    }

    private static IReadOnlyList<EmbeddedSheetImageDto> Images(ZipArchive archive, string sheetPath,
        XDocument worksheet, Dictionary<string, EmbeddedDocumentMedia> media)
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
                var width = Emu((long?)extent?.Attribute("cx")) ?? Math.Max(80, (CoordinatePart(to, "col") - CoordinatePart(from, "col")) * 100);
                var height = Emu((long?)extent?.Attribute("cy")) ?? Math.Max(80, (CoordinatePart(to, "row") - CoordinatePart(from, "row")) * 21);
                result.Add(new(mediaId, CoordinatePart(from, "row"), CoordinatePart(from, "col"),
                    Emu(LongPart(from, "colOff")) ?? 0, Emu(LongPart(from, "rowOff")) ?? 0,
                    Math.Clamp(width, 16, 2400), Math.Clamp(height, 16, 2400),
                    (string?)anchor.Descendants(SpreadsheetDrawing + "cNvPr").FirstOrDefault()?.Attribute("descr")));
            }
        }
        return result;
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

    private static (string Display, string? Raw) CellValue(XElement cell, IReadOnlyList<string> strings, string? format)
    {
        var type = (string?)cell.Attribute("t");
        var raw = (string?)cell.Element(Main + "v") ?? string.Concat(cell.Descendants(Main + "t").Select(value => value.Value));
        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < strings.Count) return (strings[index], raw);
        if (type == "b") return (raw == "1" ? "TRUE" : "FALSE", raw);
        if (type is "str" or "inlineStr" || string.IsNullOrEmpty(raw)) return (raw ?? string.Empty, raw);
        return (FormatNumber(raw, format), raw);
    }

    private static string FormatNumber(string value, string? format)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;
        if (format?.Contains('%') == true) return (number * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        if (format?.Contains('+') == true && number > 0) return "+" + number.ToString("0.##", CultureInfo.InvariantCulture);
        var decimalPlaces = format is null ? -1 : format.Split(';')[0].Split('.').ElementAtOrDefault(1)?.TakeWhile(character => character is '0' or '#').Count() ?? 0;
        if (decimalPlaces > 0) return number.ToString($"0.{new string('#', Math.Min(decimalPlaces, 8))}", CultureInfo.InvariantCulture);
        return Math.Abs(number % 1) < .0000001 ? number.ToString("0", CultureInfo.InvariantCulture) : number.ToString("0.########", CultureInfo.InvariantCulture);
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
    private static double? AttributeDouble(XElement value, string name) =>
        double.TryParse((string?)value.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static int ExcelWidth(double width) => Math.Clamp((int)Math.Round(width * 7 + 5), 24, 600);
    private static string? SafeLink(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
    private readonly record struct RawCell(string? Value, string? RawValue, int Style);

    private sealed class WorkbookStyles
    {
        private readonly CellStyle[] _cells;
        private readonly string?[] _formats;
        private WorkbookStyles(CellStyle[] cells, string?[] formats) { _cells = cells; _formats = formats; }
        public CellStyle Cell(int index) => index >= 0 && index < _cells.Length ? _cells[index] : CellStyle.Default;
        public string? Format(int index) => Cell(index).NumberFormatId is var id && id >= 0 && id < _formats.Length ? _formats[id] : null;

        public static WorkbookStyles Read(XDocument? document)
        {
            if (document?.Root is null) return new([CellStyle.Default], []);
            var fonts = document.Root.Element(Main + "fonts")?.Elements(Main + "font").Select(Font).ToArray() ?? [FontStyle.Default];
            var fills = document.Root.Element(Main + "fills")?.Elements(Main + "fill").Select(Fill).ToArray() ?? [null];
            var borders = document.Root.Element(Main + "borders")?.Elements(Main + "border").Select(Border).ToArray() ?? [BorderSet.Default];
            var formats = new string?[512];
            foreach (var format in document.Descendants(Main + "numFmt"))
                if (AttributeInt(format, "numFmtId", -1) is >= 0 and < 512 and var id) formats[id] = (string?)format.Attribute("formatCode");
            var cells = document.Root.Element(Main + "cellXfs")?.Elements(Main + "xf").Select(value =>
            {
                var font = fonts.ElementAtOrDefault(AttributeInt(value, "fontId", 0)) ?? FontStyle.Default;
                var background = fills.ElementAtOrDefault(AttributeInt(value, "fillId", 0)) ?? "#FFFFFF";
                var border = borders.ElementAtOrDefault(AttributeInt(value, "borderId", 0)) ?? BorderSet.Default;
                var alignment = value.Element(Main + "alignment");
                return new CellStyle(font.Bold, font.Italic, font.Underline, FontScale(font.Size), font.Color,
                    background, Horizontal(alignment), Vertical(alignment), border.Top, border.Right, border.Bottom,
                    border.Left, AttributeInt(value, "numFmtId", 0), font.Family, font.Size * 96d / 72d,
                    font.Bold ? 700 : 400, (bool?)alignment?.Attribute("wrapText"));
            }).ToArray() ?? [CellStyle.Default];
            return new(cells, formats);
        }

        private static FontStyle Font(XElement value) => new(value.Element(Main + "b") is not null,
            value.Element(Main + "i") is not null, value.Element(Main + "u") is not null,
            AttributeDouble(value.Element(Main + "sz") ?? new XElement("none"), "val") ?? 10,
            Rgb(value.Element(Main + "color")), (string?)value.Element(Main + "name")?.Attribute("val"));
        private static string? Fill(XElement value)
        {
            var pattern = value.Element(Main + "patternFill");
            return (string?)pattern?.Attribute("patternType") == "solid" ? Rgb(pattern.Element(Main + "fgColor")) : null;
        }
        private static BorderSet Border(XElement value) => new(Edge(value.Element(Main + "top")),
            Edge(value.Element(Main + "right")), Edge(value.Element(Main + "bottom")), Edge(value.Element(Main + "left")));
        private static BorderEdge Edge(XElement? value) => new((string?)value?.Attribute("style") switch
        {
            null or "none" => EmbeddedSheetBorderStyle.None,
            "medium" or "mediumDashed" => EmbeddedSheetBorderStyle.Medium,
            "thick" or "double" => EmbeddedSheetBorderStyle.Thick,
            "dashed" or "dashDot" or "dashDotDot" => EmbeddedSheetBorderStyle.Dashed,
            "dotted" => EmbeddedSheetBorderStyle.Dotted,
            _ => EmbeddedSheetBorderStyle.Thin
        }, Rgb(value?.Element(Main + "color")));
        private static EmbeddedDocumentTextAlignment Horizontal(XElement? value) => (string?)value?.Attribute("horizontal") switch
        { "center" or "centerContinuous" => EmbeddedDocumentTextAlignment.Center, "right" => EmbeddedDocumentTextAlignment.End, "justify" => EmbeddedDocumentTextAlignment.Justify, _ => EmbeddedDocumentTextAlignment.Start };
        private static EmbeddedSheetVerticalAlignment Vertical(XElement? value) => (string?)value?.Attribute("vertical") switch
        { "top" => EmbeddedSheetVerticalAlignment.Top, "bottom" => EmbeddedSheetVerticalAlignment.Bottom, _ => EmbeddedSheetVerticalAlignment.Middle };
        private static EmbeddedSheetFontSize FontScale(double size) => size switch
        { <= 9 => EmbeddedSheetFontSize.Small, <= 11 => EmbeddedSheetFontSize.Normal, <= 14 => EmbeddedSheetFontSize.Medium, <= 18 => EmbeddedSheetFontSize.Large, _ => EmbeddedSheetFontSize.Heading };
        private static string? Rgb(XElement? value)
        {
            var rgb = ((string?)value?.Attribute("rgb"))?.TrimStart('#');
            if (rgb is { Length: 8 }) rgb = rgb[2..];
            return rgb is { Length: 6 } && rgb.All(Uri.IsHexDigit) ? $"#{rgb.ToUpperInvariant()}" : null;
        }
        private sealed record FontStyle(bool Bold, bool Italic, bool Underline, double Size, string? Color,
            string? Family)
        { public static readonly FontStyle Default = new(false, false, false, 10, null, null); }
        private sealed record BorderSet(BorderEdge Top, BorderEdge Right, BorderEdge Bottom, BorderEdge Left)
        { public static readonly BorderSet Default = new(BorderEdge.Default, BorderEdge.Default, BorderEdge.Default, BorderEdge.Default); }
        public sealed record BorderEdge(EmbeddedSheetBorderStyle Style, string? Color)
        { public static readonly BorderEdge Default = new(EmbeddedSheetBorderStyle.None, null); }
        public sealed record CellStyle(bool Bold, bool Italic, bool Underline, EmbeddedSheetFontSize FontSize,
            string? Foreground, string? Background, EmbeddedDocumentTextAlignment Horizontal,
            EmbeddedSheetVerticalAlignment Vertical, BorderEdge Top, BorderEdge Right, BorderEdge Bottom,
            BorderEdge Left, int NumberFormatId, string? FontFamily, double? FontSizePx, int? FontWeight,
            bool? WrapText)
        {
            public static readonly CellStyle Default = new(false, false, false, EmbeddedSheetFontSize.Normal,
                null, null, EmbeddedDocumentTextAlignment.Start, EmbeddedSheetVerticalAlignment.Middle,
                BorderEdge.Default, BorderEdge.Default, BorderEdge.Default, BorderEdge.Default, 0, null, null, null, null);
        }
    }
}

public sealed record GoogleSheetsXlsxParseResult(EmbeddedSheetDto Sheet,
    IReadOnlyDictionary<string, EmbeddedDocumentMedia> Media);
