using System.Text;
using System.Text.RegularExpressions;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record EmojiTextPart(string Text, Guid? EmojiId = null, string? EmojiName = null,
    string? StandardArtworkKey = null, string? StandardGlyph = null, string? StandardName = null);

public static partial class EmojiMessageParser
{
    [GeneratedRegex("<:([a-z0-9_]{2,32}):([0-9a-fA-F]{32})>", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static IReadOnlyList<EmojiTextPart> Parse(string content)
    {
        var result = new List<EmojiTextPart>(); var position = 0;
        foreach (Match match in TokenPattern().Matches(content))
        {
            if (match.Index > position) AddStandardParts(result, content[position..match.Index]);
            result.Add(new(string.Empty, Guid.ParseExact(match.Groups[2].Value, "N"), match.Groups[1].Value));
            position = match.Index + match.Length;
        }
        if (position < content.Length) AddStandardParts(result, content[position..]);
        return result;
    }

    public static IReadOnlyList<EmojiTextPart> ParseStandard(string content)
    {
        var result = new List<EmojiTextPart>();
        if (content.Length > 0) AddStandardParts(result, content);
        return result;
    }

    private static void AddStandardParts(List<EmojiTextPart> result, string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var match = StandardEmojiCatalog.MatchAt(text, position);
            if (match is not null)
            {
                result.Add(new(string.Empty, StandardArtworkKey: match.ArtworkKey, StandardGlyph: match.Glyph,
                    StandardName: match.Name));
                position += match.Glyph.Length;
                continue;
            }
            var next = position + Rune.GetRuneAt(text, position).Utf16SequenceLength;
            while (next < text.Length && StandardEmojiCatalog.MatchAt(text, next) is null) next++;
            result.Add(new(text[position..next]));
            position = next;
        }
    }

    public static bool IsLargeEmojiOnly(string content, int maximum = 5)
    {
        var customCount = TokenPattern().Matches(content).Count;
        var remaining = TokenPattern().Replace(content, string.Empty);
        var unicodeCount = 0;
        for (var position = 0; position < remaining.Length;)
        {
            if (char.IsWhiteSpace(remaining[position])) { position++; continue; }
            var emoji = StandardEmojiCatalog.MatchAt(remaining, position);
            if (emoji is not null) { unicodeCount++; position += emoji.Glyph.Length; continue; }
            var rune = Rune.GetRuneAt(remaining, position);
            if (Rune.IsWhiteSpace(rune) || rune.Value is 0xFE0F or 0x200D || rune.Value is >= 0x1F3FB and <= 0x1F3FF)
            { position += rune.Utf16SequenceLength; continue; }
            if (!IsEmojiRune(rune.Value)) return false;
            unicodeCount++; position += rune.Utf16SequenceLength;
        }
        var total = customCount + unicodeCount;
        return total is > 0 && total <= maximum;
    }

    private static bool IsEmojiRune(int value) => value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or
        0x2764 or 0x00A9 or 0x00AE;
}
