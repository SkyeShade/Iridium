using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record MessageContentSegment(string Text, CommunityMentionDto? Mention);

public static class MessageContentSegments
{
    public static IReadOnlyList<MessageContentSegment> Parse(
        string content,
        IReadOnlyList<CommunityMentionDto>? mentions)
    {
        if (mentions is null || mentions.Count == 0) return [new(content, null)];
        var result = new List<MessageContentSegment>();
        var cursor = 0;
        foreach (var mention in mentions.OrderBy(value => value.Start))
        {
            if (mention.Start < cursor || mention.Start < 0 || mention.Start + mention.Length > content.Length) continue;
            if (mention.Start > cursor) result.Add(new(content[cursor..mention.Start], null));
            result.Add(new(string.Empty, mention));
            cursor = mention.Start + mention.Length;
        }
        if (cursor < content.Length) result.Add(new(content[cursor..], null));
        return result;
    }
}
