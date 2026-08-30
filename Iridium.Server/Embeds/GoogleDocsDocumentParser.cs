using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Iridium.Protocol;

namespace Iridium.Server.Embeds;

public sealed record GoogleDocsMediaReference(string MediaId, Uri? Source = null, byte[]? InlineBytes = null,
    string? InlineContentType = null);
public sealed record GoogleDocsParseMetrics(int DomNodes, int Blocks, int Spans, int Images, int TextCharacters);
public sealed record GoogleDocsParseResult(EmbeddedDocumentDto Document,
    IReadOnlyDictionary<string, GoogleDocsMediaReference> Media, GoogleDocsParseMetrics Metrics);
public sealed class GoogleDocsDocumentTooLargeException(string message) : Exception(message);

/// <summary>Converts provider HTML into Iridium's deliberately small, inert document vocabulary.</summary>
public sealed class GoogleDocsDocumentParser
{
    public const int MaximumDomNodes = 250_000;
    public const int MaximumBlocks = 50_000;
    public const int MaximumSpans = 250_000;
    public const int MaximumTextCharacters = 8_000_000;
    public const int MaximumImages = 2_000;
    private const int MaximumInlineImageBytes = 8 * 1024 * 1024;
    private const int MaximumDepth = 12;
    private const int MaximumListDepth = 6;
    private static readonly HashSet<string> IgnoredElements = new(StringComparer.OrdinalIgnoreCase)
    { "SCRIPT", "IFRAME", "OBJECT", "EMBED", "FORM", "INPUT", "BUTTON", "TEXTAREA", "SELECT", "STYLE", "NOSCRIPT" };
    private static readonly (EmbeddedDocumentTextColor Color, Rgb Source)[] TextColorPalette =
    {
        (EmbeddedDocumentTextColor.Red, new(217, 48, 37)),
        (EmbeddedDocumentTextColor.Orange, new(230, 124, 0)),
        (EmbeddedDocumentTextColor.Yellow, new(246, 191, 38)),
        (EmbeddedDocumentTextColor.Green, new(24, 128, 56)),
        (EmbeddedDocumentTextColor.Teal, new(0, 137, 123)),
        (EmbeddedDocumentTextColor.Blue, new(26, 115, 232)),
        (EmbeddedDocumentTextColor.Purple, new(147, 52, 230)),
        (EmbeddedDocumentTextColor.Pink, new(216, 27, 96))
    };

    public GoogleDocsParseResult? Parse(string html, string documentId, Uri sourceUri)
    {
        var source = new HtmlParser().ParseDocument(html);
        if (source.All.Length > MaximumDomNodes)
            throw new GoogleDocsDocumentTooLargeException($"Document has more than {MaximumDomNodes} DOM nodes.");
        if (source.Body is null) return null;
        var context = new ParseContext(documentId, sourceUri, ReadClassFormats(source));
        var blocks = NormalizeBlocks(ParseChildren(source.Body, context, 0));
        if (blocks.Count == 0) return null;
        var counts = CountDocument(blocks, source.All.Length);
        if (counts.Blocks > MaximumBlocks || counts.Spans > MaximumSpans ||
            counts.TextCharacters > MaximumTextCharacters || counts.Images > MaximumImages)
            throw new GoogleDocsDocumentTooLargeException("Normalized document complexity exceeds its safety limit.");
        return new(new(blocks), context.Media, counts);
    }

    private static GoogleDocsParseMetrics CountDocument(IReadOnlyList<EmbeddedDocumentBlockDto> blocks, int domNodes)
    {
        var blockCount = 0; var spanCount = 0; var imageCount = 0; var textCharacters = 0;
        void CountInlines(IReadOnlyList<EmbeddedDocumentInlineDto> values)
        {
            foreach (var value in values)
            {
                spanCount++;
                if (value is EmbeddedDocumentTextDto text) textCharacters += text.Text.Length;
                else if (value is EmbeddedDocumentLinkDto link) CountInlines(link.Content);
            }
        }
        void CountBlocks(IReadOnlyList<EmbeddedDocumentBlockDto> values)
        {
            foreach (var block in values)
            {
                blockCount++;
                switch (block)
                {
                    case EmbeddedDocumentParagraphDto paragraph: CountInlines(paragraph.Content); break;
                    case EmbeddedDocumentHeadingDto heading: CountInlines(heading.Content); break;
                    case EmbeddedDocumentImageDto: imageCount++; break;
                    case EmbeddedDocumentListDto list:
                        foreach (var item in list.Items) CountBlocks(item.Blocks);
                        break;
                    case EmbeddedDocumentTableDto table:
                        foreach (var row in table.Rows)
                            foreach (var cell in row.Cells) CountBlocks(cell.Blocks);
                        break;
                }
            }
        }
        CountBlocks(blocks);
        return new(domNodes, blockCount, spanCount, imageCount, textCharacters);
    }

    private static IEnumerable<EmbeddedDocumentBlockDto> ParseChildren(IElement parent, ParseContext context, int depth)
    {
        if (depth > MaximumDepth) yield break;
        foreach (var child in parent.Children)
            foreach (var block in ParseElement(child, context, depth + 1)) yield return block;
    }

    private static IEnumerable<EmbeddedDocumentBlockDto> ParseElement(IElement element, ParseContext context, int depth)
    {
        if (depth > MaximumDepth || IgnoredElements.Contains(element.TagName)) yield break;
        var tag = element.TagName;
        if (tag.Length == 2 && tag[0] == 'H' && char.IsAsciiDigit(tag[1]))
        {
            var content = InlineContent(element, context);
            if (content.Count > 0) yield return new EmbeddedDocumentHeadingDto(Math.Clamp(tag[1] - '0', 1, 6), content,
                Alignment(element, context));
            yield break;
        }
        switch (tag)
        {
            case "P": case "BLOCKQUOTE": case "PRE":
            {
                var content = InlineContent(element, context);
                if (content.Count > 0) yield return new EmbeddedDocumentParagraphDto(content, Alignment(element, context));
                var images = new List<EmbeddedDocumentImageDto>();
                foreach (var imageElement in element.QuerySelectorAll("img"))
                    if (TryImage(imageElement, context, out var block)) images.Add(block);
                foreach (var parsedImage in images) yield return parsedImage;
                if (tag == "P" && content.Count == 0 && images.Count == 0 &&
                    !element.QuerySelectorAll("img").Any() && IsSemanticBlankParagraph(element))
                    yield return new EmbeddedDocumentSpacerDto();
                yield break;
            }
            case "IMG":
                if (TryImage(element, context, out var image)) yield return image;
                yield break;
            case "UL": case "OL":
                if (ParseList(element, context, depth, 0) is { Items.Count: > 0 } list) yield return list;
                yield break;
            case "TABLE":
                if (ParseTable(element, context, depth) is { Rows.Count: > 0 } table) yield return table;
                yield break;
            case "HR": yield return new EmbeddedDocumentHorizontalRuleDto(); yield break;
            default:
            {
                if (element.Children.Any(IsBlockElement))
                {
                    foreach (var block in ParseChildren(element, context, depth)) yield return block;
                }
                else
                {
                    var content = InlineContent(element, context);
                    if (content.Count > 0) yield return new EmbeddedDocumentParagraphDto(content, Alignment(element, context));
                }
                yield break;
            }
        }
    }

    private static EmbeddedDocumentListDto? ParseList(IElement list, ParseContext context, int depth, int listDepth)
    {
        if (listDepth >= MaximumListDepth) return null;
        var items = new List<EmbeddedDocumentListItemDto>();
        foreach (var item in list.Children.Where(value => value.TagName == "LI"))
        {
            var blocks = new List<EmbeddedDocumentBlockDto>();
            var content = InlineContent(item, context, child => child.TagName is not ("UL" or "OL"));
            if (content.Count > 0) blocks.Add(new EmbeddedDocumentParagraphDto(content));
            foreach (var nested in item.Children.Where(value => value.TagName is "UL" or "OL"))
                if (ParseList(nested, context, depth + 1, listDepth + 1) is { } nestedList) blocks.Add(nestedList);
            if (blocks.Count > 0) items.Add(new(blocks));
        }
        return new(list.TagName == "OL", items);
    }

    private static EmbeddedDocumentTableDto ParseTable(IElement table, ParseContext context, int depth)
    {
        var rows = new List<EmbeddedDocumentTableRowDto>();
        foreach (var row in table.QuerySelectorAll("tr").Take(500))
        {
            var cells = new List<EmbeddedDocumentTableCellDto>();
            foreach (var cell in row.Children.Where(value => value.TagName is "TD" or "TH").Take(50))
            {
                var blocks = ParseChildren(cell, context, depth + 1).ToList();
                if (blocks.Count == 0)
                {
                    var content = InlineContent(cell, context);
                    if (content.Count > 0) blocks.Add(new EmbeddedDocumentParagraphDto(content));
                }
                cells.Add(new(cell.TagName == "TH", PositiveInt(cell, "colspan"), PositiveInt(cell, "rowspan"), blocks));
            }
            if (cells.Count > 0) rows.Add(new(cells));
        }
        return new(rows);
    }

    private static IEnumerable<EmbeddedDocumentInlineDto> ParseInlines(IElement parent, ParseContext context,
        TextFormat inherited, Func<IElement, bool>? include = null)
    {
        foreach (var node in parent.ChildNodes)
        {
            if (node is IText text)
            {
                var normalized = NormalizeText(text.Data);
                if (normalized.Length > 0) yield return new EmbeddedDocumentTextDto(normalized,
                    inherited.Bold, inherited.Italic, inherited.Underline,
                    inherited.TextColor ?? EmbeddedDocumentTextColor.Default);
                continue;
            }
            if (node is not IElement element || IgnoredElements.Contains(element.TagName) ||
                include is not null && !include(element) || element.TagName == "IMG") continue;
            if (element.TagName == "BR") { yield return new EmbeddedDocumentLineBreakDto(); continue; }
            var format = inherited.Merge(Format(element, context));
            if (element.TagName == "A")
            {
                var linked = ParseInlines(element, context, format).ToList();
                if (TrySafeLink(element.GetAttribute("href"), context.SourceUri, out var url) && linked.Count > 0)
                    yield return new EmbeddedDocumentLinkDto(url.AbsoluteUri, linked);
                else foreach (var value in linked) yield return value;
                continue;
            }
            foreach (var value in ParseInlines(element, context, format)) yield return value;
        }
    }

    private static bool TryImage(IElement element, ParseContext context, out EmbeddedDocumentImageDto image)
    {
        image = null!;
        var sourceValue = element.GetAttribute("src");
        GoogleDocsMediaReference reference;
        if (TryInlineImage(sourceValue, context.DocumentId, out reference)) { }
        else if (TryResolveUri(sourceValue, context.SourceUri, out var source) && AllowedImageHost(source))
        {
            var hash = MediaId(context.DocumentId, Encoding.UTF8.GetBytes(source.AbsoluteUri));
            reference = new(hash, source);
        }
        else return false;
        context.Media.TryAdd(reference.MediaId, reference);
        var dimensions = ImageDimensions(element);
        var alignment = ImageAlignment(element, context);
        image = new(reference.MediaId, CleanAlt(element.GetAttribute("alt")), dimensions.Width, dimensions.Height, alignment);
        return true;
    }

    private static bool TryInlineImage(string? value, string documentId, out GoogleDocsMediaReference reference)
    {
        reference = null!;
        if (value is null || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var separator = value.IndexOf(',');
        if (separator is < 1 or > 80) return false;
        var metadata = value[5..separator].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (metadata.Length != 2 || !metadata[1].Equals("base64", StringComparison.OrdinalIgnoreCase)) return false;
        var contentType = metadata[0].ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) return false;
        try
        {
            var bytes = Convert.FromBase64String(value[(separator + 1)..]);
            if (bytes.Length is 0 or > MaximumInlineImageBytes) return false;
            reference = new(MediaId(documentId, bytes), InlineBytes: bytes, InlineContentType: contentType);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static (int? Width, int? Height) ImageDimensions(IElement element)
    {
        var width = NullablePositiveInt(element, "width");
        var height = NullablePositiveInt(element, "height");
        if ((width is null || height is null) && element.GetAttribute("style") is { Length: > 0 } inline &&
            new CssParser().ParseDeclaration(inline) is { } declaration)
        {
            width ??= CssPixels(declaration.GetPropertyValue("width"));
            height ??= CssPixels(declaration.GetPropertyValue("height"));
        }
        return (width, height);
    }

    private static EmbeddedDocumentTextAlignment ImageAlignment(IElement image, ParseContext context)
    {
        for (var ancestor = image.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            if (Format(ancestor, context).Alignment is { } alignment) return alignment;
        return EmbeddedDocumentTextAlignment.Center;
    }

    private static int? CssPixels(string value)
    {
        value = value.Trim();
        if (!value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
            !double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) ||
            pixels is <= 0 or > 10_000) return null;
        return Math.Max(1, (int)Math.Round(pixels, MidpointRounding.AwayFromZero));
    }

    private static string MediaId(string documentId, ReadOnlySpan<byte> identity)
    {
        var prefix = Encoding.UTF8.GetBytes(documentId + "\n");
        var combined = new byte[prefix.Length + identity.Length];
        prefix.CopyTo(combined, 0); identity.CopyTo(combined.AsSpan(prefix.Length));
        return Convert.ToHexString(SHA256.HashData(combined))[..32].ToLowerInvariant();
    }

    private static List<EmbeddedDocumentInlineDto> InlineContent(IElement element, ParseContext context,
        Func<IElement, bool>? include = null) =>
        NormalizeInlines(ParseInlines(element, context, Format(element, context), include));

    private static List<EmbeddedDocumentInlineDto> NormalizeInlines(IEnumerable<EmbeddedDocumentInlineDto> values)
    {
        var output = new List<EmbeddedDocumentInlineDto>();
        var consecutiveBreaks = 0;
        foreach (var value in values)
        {
            if (value is EmbeddedDocumentLineBreakDto)
            {
                if (output.Count == 0 || consecutiveBreaks >= 2) continue;
                consecutiveBreaks++; output.Add(value); continue;
            }
            consecutiveBreaks = 0;
            if (value is EmbeddedDocumentTextDto { Text.Length: 0 }) continue;
            output.Add(value);
        }
        while (output.LastOrDefault() is EmbeddedDocumentLineBreakDto) output.RemoveAt(output.Count - 1);
        return output;
    }

    private static List<EmbeddedDocumentBlockDto> NormalizeBlocks(IEnumerable<EmbeddedDocumentBlockDto> values)
    {
        const int maximumConsecutiveBlankLines = 3;
        var output = new List<EmbeddedDocumentBlockDto>();
        var blankLines = 0;
        foreach (var value in values.Where(HasContent))
        {
            if (value is EmbeddedDocumentSpacerDto spacer)
            {
                if (output.Count == 0) continue;
                var accepted = Math.Min(Math.Max(0, spacer.Lines), maximumConsecutiveBlankLines - blankLines);
                if (accepted > 0) output.Add(new EmbeddedDocumentSpacerDto(accepted));
                blankLines += accepted;
                continue;
            }
            blankLines = 0;
            output.Add(value);
        }
        while (output.LastOrDefault() is EmbeddedDocumentSpacerDto) output.RemoveAt(output.Count - 1);
        return output;
    }

    // Google export keeps Enter-created lines as sibling paragraphs, Shift+Enter as BR, and an
    // intentional empty Enter-created line as an empty P (sometimes containing only a BR/span).
    // Empty non-paragraph wrappers are layout noise and never reach this predicate.
    private static bool IsSemanticBlankParagraph(IElement paragraph) =>
        string.IsNullOrWhiteSpace(paragraph.TextContent.Replace('\u00a0', ' ')) &&
        paragraph.QuerySelector("hr,table,ul,ol") is null;

    private static Dictionary<string, TextFormat> ReadClassFormats(IDocument document)
    {
        var formats = new Dictionary<string, TextFormat>(StringComparer.Ordinal);
        var parser = new CssParser();
        foreach (var style in document.QuerySelectorAll("style"))
        {
            var sheet = parser.ParseStyleSheet(style.TextContent);
            foreach (var rule in sheet.Rules.OfType<ICssStyleRule>())
            {
                var format = FromCss(rule.Style);
                foreach (var selector in rule.SelectorText.Split(','))
                {
                    var value = selector.Trim();
                    if (value.Length > 1 && value[0] == '.' && value[1..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                        formats[value[1..]] = formats.GetValueOrDefault(value[1..]).Merge(format);
                }
            }
        }
        return formats;
    }

    private static TextFormat Format(IElement element, ParseContext context)
    {
        var result = new TextFormat(element.TagName is "B" or "STRONG", element.TagName is "I" or "EM",
            element.TagName == "U", null);
        foreach (var name in element.ClassList)
            if (context.ClassFormats.TryGetValue(name, out var value)) result = result.Merge(value);
        if (element.GetAttribute("style") is { Length: > 0 } inline)
            if (new CssParser().ParseDeclaration(inline) is { } declaration)
                result = result.Merge(FromCss(declaration));
        return result;
    }

    private static TextFormat FromCss(ICssStyleDeclaration style)
    {
        var weight = style.GetPropertyValue("font-weight").Trim();
        var bold = weight.Equals("bold", StringComparison.OrdinalIgnoreCase) ||
            int.TryParse(weight, out var numericWeight) && numericWeight >= 600;
        var decoration = style.GetPropertyValue("text-decoration");
        var alignment = style.GetPropertyValue("text-align").Trim().ToLowerInvariant() switch
        { "center" => EmbeddedDocumentTextAlignment.Center, "right" or "end" => EmbeddedDocumentTextAlignment.End,
          "justify" => EmbeddedDocumentTextAlignment.Justify, _ => (EmbeddedDocumentTextAlignment?)null };
        return new(bold, style.GetPropertyValue("font-style").Contains("italic", StringComparison.OrdinalIgnoreCase),
            decoration.Contains("underline", StringComparison.OrdinalIgnoreCase), alignment,
            TryTextColor(style.GetPropertyValue("color"), out var textColor) ? textColor : null);
    }

    private static bool TryTextColor(string? value, out EmbeddedDocumentTextColor color)
    {
        color = EmbeddedDocumentTextColor.Default;
        if (!TryRgb(value, out var source)) return false;
        color = MapTextColor(source);
        return true;
    }

    private static bool TryRgb(string? value, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text[0] == '#')
        {
            if (text.Length == 4 && TryHex(text[1], out var r) && TryHex(text[2], out var g) && TryHex(text[3], out var b))
            {
                color = new((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                return true;
            }
            if (text.Length == 7 && byte.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var red) &&
                byte.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) &&
                byte.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
            {
                color = new(red, green, blue);
                return true;
            }
            return false;
        }
        var open = text.IndexOf('(');
        if (open < 1 || !text.EndsWith(')')) return false;
        var function = text[..open].Trim();
        if (!function.Equals("rgb", StringComparison.OrdinalIgnoreCase) &&
            !function.Equals("rgba", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = text[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != (function.Equals("rgba", StringComparison.OrdinalIgnoreCase) ? 4 : 3) ||
            !TryColorChannel(parts[0], out var rgbRed) || !TryColorChannel(parts[1], out var rgbGreen) ||
            !TryColorChannel(parts[2], out var rgbBlue)) return false;
        var alpha = 1d;
        if (parts.Length == 4 && !TryAlpha(parts[3], out alpha)) return false;
        color = new(CompositeOnWhite(rgbRed, alpha), CompositeOnWhite(rgbGreen, alpha),
            CompositeOnWhite(rgbBlue, alpha));
        return true;
    }

    private static EmbeddedDocumentTextColor MapTextColor(Rgb source)
    {
        var maximum = Math.Max(source.Red, Math.Max(source.Green, source.Blue));
        var minimum = Math.Min(source.Red, Math.Min(source.Green, source.Blue));
        var chroma = maximum - minimum;
        if (maximum <= 55 || chroma <= 28 && (source.Red + source.Green + source.Blue) / 3 <= 120 ||
            minimum >= 235 && chroma <= 20) return EmbeddedDocumentTextColor.Default;
        if (chroma <= 28) return EmbeddedDocumentTextColor.Gray;

        return TextColorPalette.MinBy(entry => ColorDistance(source, entry.Source)).Color;
    }

    private static int ColorDistance(Rgb left, Rgb right)
    {
        var redMean = (left.Red + right.Red) / 2;
        var red = left.Red - right.Red;
        var green = left.Green - right.Green;
        var blue = left.Blue - right.Blue;
        return ((512 + redMean) * red * red >> 8) + 4 * green * green +
            ((767 - redMean) * blue * blue >> 8);
    }

    private static bool TryHex(char value, out int result)
    {
        result = value is >= '0' and <= '9' ? value - '0' :
            value is >= 'a' and <= 'f' ? value - 'a' + 10 :
            value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;
        return result >= 0;
    }

    private static bool TryColorChannel(string value, out byte channel)
    {
        channel = 0;
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percentage) && percentage is >= 0 and <= 100)
        {
            channel = (byte)Math.Round(percentage * 2.55, MidpointRounding.AwayFromZero);
            return true;
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number is < 0 or > 255) return false;
        channel = (byte)Math.Round(number, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryAlpha(string value, out double alpha)
    {
        alpha = 0;
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percentage) && percentage is >= 0 and <= 100)
        {
            alpha = percentage / 100;
            return true;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha) &&
            alpha is >= 0 and <= 1;
    }

    private static byte CompositeOnWhite(byte channel, double alpha) =>
        (byte)Math.Round(channel * alpha + 255 * (1 - alpha), MidpointRounding.AwayFromZero);

    private static EmbeddedDocumentTextAlignment Alignment(IElement element, ParseContext context) =>
        Format(element, context).Alignment ?? EmbeddedDocumentTextAlignment.Start;
    private static bool IsBlockElement(IElement value) => value.TagName is "P" or "DIV" or "SECTION" or "ARTICLE" or
        "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "UL" or "OL" or "TABLE" or "HR" or "IMG";
    private static bool HasContent(EmbeddedDocumentBlockDto block) => block switch
    { EmbeddedDocumentParagraphDto value => value.Content.Count > 0,
      EmbeddedDocumentHeadingDto value => value.Content.Count > 0,
      EmbeddedDocumentListDto value => value.Items.Count > 0,
      EmbeddedDocumentTableDto value => value.Rows.Count > 0, _ => true };
    private static string NormalizeText(string value)
    {
        var output = new StringBuilder(value.Length); var whitespace = false;
        foreach (var character in value.Replace('\u00a0', ' '))
        {
            if (char.IsWhiteSpace(character)) { whitespace = output.Length > 0; continue; }
            if (whitespace) output.Append(' ');
            whitespace = false; output.Append(character);
        }
        if (whitespace && output.Length > 0) output.Append(' ');
        return output.ToString();
    }
    private static bool TrySafeLink(string? value, Uri baseUri, out Uri uri) =>
        TryResolveUri(value, baseUri, out uri) && uri.Scheme is "http" or "https";
    private static bool TryResolveUri(string? value, Uri baseUri, out Uri uri) =>
        Uri.TryCreate(baseUri, value, out uri!) && uri.IsAbsoluteUri;
    private static bool AllowedImageHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static int PositiveInt(IElement element, string name) => NullablePositiveInt(element, name) ?? 1;
    private static int? NullablePositiveInt(IElement element, string name) =>
        int.TryParse(element.GetAttribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value is > 0 and <= 10_000 ? value : null;
    private static string? CleanAlt(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(300, value.Trim().Length)];

    private sealed class ParseContext(string documentId, Uri sourceUri, Dictionary<string, TextFormat> formats)
    {
        public string DocumentId { get; } = documentId;
        public Uri SourceUri { get; } = sourceUri;
        public Dictionary<string, TextFormat> ClassFormats { get; } = formats;
        public Dictionary<string, GoogleDocsMediaReference> Media { get; } = new(StringComparer.Ordinal);
    }
    private readonly record struct Rgb(byte Red, byte Green, byte Blue);
    private readonly record struct TextFormat(bool Bold, bool Italic, bool Underline,
        EmbeddedDocumentTextAlignment? Alignment, EmbeddedDocumentTextColor? TextColor = null)
    {
        public TextFormat Merge(TextFormat other) => new(Bold || other.Bold, Italic || other.Italic,
            Underline || other.Underline, other.Alignment ?? Alignment, other.TextColor ?? TextColor);
    }
}
