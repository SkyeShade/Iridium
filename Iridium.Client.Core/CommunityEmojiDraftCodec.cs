using System.Text;
using System.Text.RegularExpressions;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record CommunityEmojiDraftReference(int Start, int Length, Guid EmojiId, string Name,
    Guid CommunityId);
public sealed record CommunityEmojiDraftDocument(string Text, IReadOnlyList<CommunityEmojiDraftReference> References);

public static partial class CommunityEmojiDraftCodec
{
    public const char ObjectReplacementCharacter = '\uFFFC';
    [GeneratedRegex("<:([a-z0-9_]{2,32}):([0-9a-fA-F]{32})>", RegexOptions.CultureInvariant)]
    private static partial Regex InternalTokenPattern();
    [GeneratedRegex("(?<![a-zA-Z0-9_]):([a-z0-9_]{2,32}):", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FriendlyTokenPattern();

    public static string ToUserFacing(string content) => CommunityEmojiNames.ToUserFacing(content);

    public static int CountCharacters(string content, IReadOnlyList<CommunityEmojiDraftReference> references) =>
        content.EnumerateRunes().Count() - references.Sum(value =>
            content.Substring(value.Start, value.Length).EnumerateRunes().Count() - 1);

    public static CommunityEmojiDraftDocument Deserialize(string content,
        IReadOnlyList<AvailableCommunityEmoji> available)
    {
        var text = new StringBuilder(content.Length);
        var references = new List<CommunityEmojiDraftReference>();
        var position = 0;
        foreach (Match match in InternalTokenPattern().Matches(content))
        {
            text.Append(content, position, match.Index - position);
            var name = match.Groups[1].Value;
            var friendly = $":{name}:";
            var start = text.Length;
            text.Append(friendly);
            var id = Guid.ParseExact(match.Groups[2].Value, "N");
            var source = available.FirstOrDefault(value => value.Emoji.Id == id);
            if (source is not null) references.Add(new(start, friendly.Length, id, name, source.Community.Id));
            position = match.Index + match.Length;
        }
        text.Append(content, position, content.Length - position);
        return new(text.ToString(), references);
    }

    public static string Serialize(string userFacingContent,
        IReadOnlyList<CommunityEmojiDraftReference> explicitReferences,
        IReadOnlyList<AvailableCommunityEmoji> available, Guid? currentCommunityId)
    {
        var ordered = Order(available, currentCommunityId);
        var references = explicitReferences.ToDictionary(value => value.Start);
        var result = new StringBuilder(userFacingContent.Length);
        var position = 0;
        foreach (Match match in FriendlyTokenPattern().Matches(userFacingContent))
        {
            result.Append(userFacingContent, position, match.Index - position);
            var name = match.Groups[1].Value;
            CommunityEmojiDto? emoji = null;
            if (references.TryGetValue(match.Index, out var selected) && selected.Length == match.Length &&
                string.Equals(selected.Name, name, StringComparison.OrdinalIgnoreCase))
                emoji = available.FirstOrDefault(value => value.Emoji.Id == selected.EmojiId)?.Emoji;
            emoji ??= ordered.FirstOrDefault(value => string.Equals(value.Emoji.Name, name,
                StringComparison.OrdinalIgnoreCase))?.Emoji;
            result.Append(emoji is null ? match.Value : CommunityEmojiNames.Token(emoji.Id, emoji.Name));
            position = match.Index + match.Length;
        }
        result.Append(userFacingContent, position, userFacingContent.Length - position);
        return result.ToString();
    }

    public static string SerializeDocument(string content, IReadOnlyList<CommunityEmojiDraftReference> references)
    {
        var byPosition = references.ToDictionary(value => value.Start);
        var result = new StringBuilder(content.Length);
        for (var position = 0; position < content.Length; position++)
        {
            if (content[position] == ObjectReplacementCharacter && byPosition.TryGetValue(position, out var emoji))
                result.Append(CommunityEmojiNames.Token(emoji.EmojiId, emoji.Name));
            else
                result.Append(content[position]);
        }
        return result.ToString();
    }

    public static int MapDocumentPositionToSerialized(int position,
        IReadOnlyList<CommunityEmojiDraftReference> references) => position + references
        .Where(value => value.Start < position)
        .Sum(value => CommunityEmojiNames.Token(value.EmojiId, value.Name).Length - value.Length);

    public static IReadOnlyList<AvailableCommunityEmoji> Order(IReadOnlyList<AvailableCommunityEmoji> values,
        Guid? currentCommunityId) => values.OrderBy(value => value.Community.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.Community.Id).ThenBy(value => value.Emoji.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.Emoji.Id).ToArray();

    public static void ReconcileReferences(string previous, string current, List<CommunityEmojiDraftReference> references)
    {
        var prefix = 0;
        while (prefix < previous.Length && prefix < current.Length && previous[prefix] == current[prefix]) prefix++;
        var suffix = 0;
        while (suffix < previous.Length - prefix && suffix < current.Length - prefix &&
               previous[^(suffix + 1)] == current[^(suffix + 1)]) suffix++;
        var oldChangeEnd = previous.Length - suffix;
        var delta = current.Length - previous.Length;
        for (var index = references.Count - 1; index >= 0; index--)
        {
            var value = references[index];
            if (value.Start >= oldChangeEnd) value = value with { Start = value.Start + delta };
            else if (value.Start + value.Length > prefix) { references.RemoveAt(index); continue; }
            if (value.Start < 0 || value.Start + value.Length > current.Length ||
                !string.Equals(current.Substring(value.Start, value.Length), $":{value.Name}:",
                    StringComparison.OrdinalIgnoreCase)) references.RemoveAt(index);
            else references[index] = value;
        }
    }
}
