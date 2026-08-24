namespace Iridium.Client.Core;

public readonly record struct MessageMarkdownDelimiter(
    string Marker,
    MessageContentKind ContentKind,
    ComposerMarkdownStyle ComposerStyle,
    bool Combined = false);

public static class MessageMarkdownGrammar
{
    private static readonly string[] EscapableMarkers =
        ["***", "```", "||", "**", "__", "~~", "-#", "#", "*", "_", "`", ">", "[", "]", "\\"];

    public static IReadOnlyList<MessageMarkdownDelimiter> InlineDelimiters { get; } =
    [
        new("***", MessageContentKind.Bold, ComposerMarkdownStyle.Bold | ComposerMarkdownStyle.Italic, true),
        new("||", MessageContentKind.Spoiler, ComposerMarkdownStyle.Spoiler),
        new("**", MessageContentKind.Bold, ComposerMarkdownStyle.Bold),
        new("__", MessageContentKind.Underline, ComposerMarkdownStyle.Underline),
        new("~~", MessageContentKind.Strikethrough, ComposerMarkdownStyle.Strikethrough),
        new("`", MessageContentKind.InlineCode, ComposerMarkdownStyle.InlineCode),
        new("*", MessageContentKind.Italic, ComposerMarkdownStyle.Italic),
        new("_", MessageContentKind.Italic, ComposerMarkdownStyle.Italic)
    ];

    public static bool TryDelimiter(string source, int cursor, int end,
        out MessageMarkdownDelimiter delimiter, out int closing)
    {
        foreach (var candidate in InlineDelimiters)
        {
            if (!source.AsSpan(cursor, end - cursor).StartsWith(candidate.Marker)) continue;
            var found = FindUnescaped(source, candidate.Marker, cursor + candidate.Marker.Length, end);
            if (found < 0 || found == cursor + candidate.Marker.Length) continue;
            delimiter = candidate;
            closing = found;
            return true;
        }

        delimiter = default;
        closing = -1;
        return false;
    }

    public static int FindUnescaped(string source, string marker, int start, int end)
    {
        var cursor = start;
        while (cursor < end)
        {
            var found = source.IndexOf(marker, cursor, StringComparison.Ordinal);
            if (found < 0 || found >= end) return -1;
            var slashes = 0;
            for (var index = found - 1; index >= 0 && source[index] == '\\'; index--) slashes++;
            if (slashes % 2 == 0) return found;
            cursor = found + marker.Length;
        }
        return -1;
    }

    public static bool TryEscapedMarker(string source, int cursor, int end, out string marker)
    {
        foreach (var candidate in EscapableMarkers)
            if (source.AsSpan(cursor, end - cursor).StartsWith(candidate))
            {
                marker = candidate;
                return true;
            }
        marker = string.Empty;
        return false;
    }
}
