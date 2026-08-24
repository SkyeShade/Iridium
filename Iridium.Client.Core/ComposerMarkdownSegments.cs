namespace Iridium.Client.Core;

[Flags]
public enum ComposerMarkdownStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Strikethrough = 1 << 3,
    InlineCode = 1 << 4,
    Spoiler = 1 << 5,
    Heading1 = 1 << 6,
    Heading2 = 1 << 7,
    Heading3 = 1 << 8,
    Subtext = 1 << 9,
    Quote = 1 << 10,
    List = 1 << 11,
    Link = 1 << 12,
    CodeBlock = 1 << 13
}

public sealed record ComposerMarkdownSegment(string Text, ComposerMarkdownStyle Style, bool IsMarker = false);

public static class ComposerMarkdownSegments
{
    public static IReadOnlyList<ComposerMarkdownSegment> Parse(string source)
    {
        var result = new List<ComposerMarkdownSegment>();
        var cursor = 0;
        var fenced = false;
        while (cursor < source.Length)
        {
            var newline = source.IndexOf('\n', cursor);
            var end = newline < 0 ? source.Length : newline;
            var lineEnd = newline < 0 ? end : end + 1;
            var line = source.AsSpan(cursor, end - cursor);
            if (line.StartsWith("```") && (fenced || HasClosingFence(source, lineEnd)))
            {
                Add(source[cursor..lineEnd], ComposerMarkdownStyle.None, true, result);
                fenced = !fenced;
            }
            else if (fenced) Add(source[cursor..lineEnd], ComposerMarkdownStyle.CodeBlock, false, result);
            else
            {
                var (prefix, style) = LineStyle(line);
                if (prefix > 0) Add(source.Substring(cursor, prefix), ComposerMarkdownStyle.None, true, result);
                ParseRange(source, cursor + prefix, lineEnd, style, result);
            }
            cursor = lineEnd;
        }
        return Merge(result);
    }

    private static (int Prefix, ComposerMarkdownStyle Style) LineStyle(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("### ")) return (4, ComposerMarkdownStyle.Heading3);
        if (line.StartsWith("## ")) return (3, ComposerMarkdownStyle.Heading2);
        if (line.StartsWith("# ")) return (2, ComposerMarkdownStyle.Heading1);
        if (line.StartsWith("-# ")) return (3, ComposerMarkdownStyle.Subtext);
        if (line.StartsWith(">>> ")) return (4, ComposerMarkdownStyle.Quote);
        if (line.StartsWith("> ")) return (2, ComposerMarkdownStyle.Quote);
        var index = 0;
        while (index < line.Length && line[index] == ' ') index++;
        if (index + 2 <= line.Length && (line[index..].StartsWith("- ") || line[index..].StartsWith("* ")))
            return (index + 2, ComposerMarkdownStyle.List);
        var numberStart = index;
        while (index < line.Length && char.IsDigit(line[index])) index++;
        if (index > numberStart && index + 2 <= line.Length && line[index] == '.' && line[index + 1] == ' ')
            return (index + 2, ComposerMarkdownStyle.List);
        return (0, ComposerMarkdownStyle.None);
    }

    private static void ParseRange(string source, int start, int end, ComposerMarkdownStyle inherited,
        List<ComposerMarkdownSegment> target)
    {
        var plainStart = start;
        var cursor = start;
        while (cursor < end)
        {
            if (source[cursor] == '\\' && cursor + 1 < end &&
                MessageMarkdownGrammar.TryEscapedMarker(source, cursor + 1, end, out var escaped))
            {
                cursor += escaped.Length + 1;
                continue;
            }
            if (source[cursor] == '[' && TryLink(source, cursor, end, out var labelEnd, out var linkEnd))
            {
                Add(source[plainStart..cursor], inherited, false, target);
                Add("[", ComposerMarkdownStyle.None, true, target);
                ParseRange(source, cursor + 1, labelEnd, inherited | ComposerMarkdownStyle.Link, target);
                Add(source[labelEnd..linkEnd], ComposerMarkdownStyle.None, true, target);
                cursor = linkEnd;
                plainStart = cursor;
                continue;
            }
            if (!MessageMarkdownGrammar.TryDelimiter(source, cursor, end, out var delimiter, out var closing))
            {
                cursor++;
                continue;
            }

            Add(source[plainStart..cursor], inherited, false, target);
            Add(delimiter.Marker, ComposerMarkdownStyle.None, true, target);
            var innerStart = cursor + delimiter.Marker.Length;
            if (delimiter.ComposerStyle.HasFlag(ComposerMarkdownStyle.InlineCode))
                Add(source[innerStart..closing], inherited | delimiter.ComposerStyle, false, target);
            else
                ParseRange(source, innerStart, closing, inherited | delimiter.ComposerStyle, target);
            Add(delimiter.Marker, ComposerMarkdownStyle.None, true, target);
            cursor = closing + delimiter.Marker.Length;
            plainStart = cursor;
        }
        Add(source[plainStart..end], inherited, false, target);
    }

    private static bool TryLink(string source, int cursor, int end, out int labelEnd, out int next)
    {
        labelEnd = source.IndexOf(']', cursor + 1);
        if (labelEnd <= cursor + 1 || labelEnd + 1 >= end || source[labelEnd + 1] != '(') { next = cursor; return false; }
        var urlEnd = source.IndexOf(')', labelEnd + 2);
        if (urlEnd < 0 || urlEnd >= end) { next = cursor; return false; }
        if (!MessageContentSegments.IsSafeLink(source[(labelEnd + 2)..urlEnd].Trim()))
        { next = cursor; return false; }
        next = urlEnd + 1;
        return true;
    }

    private static bool HasClosingFence(string source, int start)
    {
        var cursor = start;
        while (cursor < source.Length)
        {
            var end = source.IndexOf('\n', cursor);
            if (end < 0) end = source.Length;
            if (source.AsSpan(cursor, end - cursor).StartsWith("```")) return true;
            cursor = end < source.Length ? end + 1 : end;
        }
        return false;
    }

    private static void Add(string text, ComposerMarkdownStyle style, bool marker,
        List<ComposerMarkdownSegment> target)
    {
        if (text.Length > 0) target.Add(new(text, style, marker));
    }

    private static IReadOnlyList<ComposerMarkdownSegment> Merge(IEnumerable<ComposerMarkdownSegment> source)
    {
        var result = new List<ComposerMarkdownSegment>();
        foreach (var segment in source)
        {
            if (result.LastOrDefault() is { } previous && previous.Style == segment.Style &&
                previous.IsMarker == segment.IsMarker)
                result[^1] = previous with { Text = previous.Text + segment.Text };
            else result.Add(segment);
        }
        return result;
    }
}
