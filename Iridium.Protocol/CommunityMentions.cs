namespace Iridium.Protocol;

public enum CommunityMentionKind
{
    Account,
    Role,
    Everyone
}

/// <summary>A stable mention target plus its character range in the plain message content.</summary>
public sealed record CommunityMentionDto(
    CommunityMentionKind Kind,
    Guid? TargetId,
    int Start,
    int Length,
    string DisplayText);

public sealed record CommunityMentionInput(
    CommunityMentionKind Kind,
    Guid? TargetId,
    int Start,
    int Length);

public sealed record CommunityMentionTextTarget(
    CommunityMentionKind Kind,
    Guid? TargetId,
    string DisplayText);

/// <summary>
/// Resolves semantic mention ranges from edited plain text. Delivery is intentionally not part of
/// this operation; notification recipients are selected only by the new-message pipeline.
/// </summary>
public static class CommunityMentionTextResolver
{
    public static IReadOnlyList<CommunityMentionInput> Resolve(string content,
        IEnumerable<CommunityMentionTextTarget> targets, int maximumTargets = 16)
    {
        if (string.IsNullOrEmpty(content) || maximumTargets <= 0) return [];
        var candidates = targets.Where(value => !string.IsNullOrWhiteSpace(value.DisplayText) &&
                value.DisplayText[0] == '@')
            .DistinctBy(value => (value.Kind, value.TargetId, value.DisplayText))
            .GroupBy(value => value.DisplayText, StringComparer.Ordinal)
            .Where(group => group.Count() == 1 || group.Key == "@everyone")
            .Select(group => group.OrderBy(value => value.Kind == CommunityMentionKind.Everyone ? 0 : 1).First())
            .OrderByDescending(value => value.DisplayText.Length)
            .ThenBy(value => value.DisplayText, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return [];

        var mentions = new List<CommunityMentionInput>();
        for (var position = 0; position < content.Length && mentions.Count < maximumTargets; position++)
        {
            if (content[position] != '@' || !HasValidPrefix(content, position) ||
                !MessageText.AllowsMentionAt(content, position)) continue;
            var match = candidates.FirstOrDefault(candidate =>
                content.AsSpan(position).StartsWith(candidate.DisplayText, StringComparison.Ordinal) &&
                HasValidSuffix(content, position + candidate.DisplayText.Length));
            if (match is null) continue;
            mentions.Add(new(match.Kind, match.TargetId, position, match.DisplayText.Length));
            position += match.DisplayText.Length - 1;
        }
        return mentions;
    }

    private static bool HasValidPrefix(string content, int position) => position == 0 ||
        char.IsWhiteSpace(content[position - 1]) || content[position - 1] is '*' or '_' or '~' or '|' or '(' or '[';

    private static bool HasValidSuffix(string content, int position) => position >= content.Length ||
        !(char.IsLetterOrDigit(content[position]) || content[position] is '_' or '-');
}

public sealed record CommunityMentionReceivedEvent(
    Guid CommunityId,
    Guid ChannelId,
    Guid MessageId,
    Guid AuthorAccountId);

public static class CommunityMentionHubContract
{
    public const string Received = "CommunityMentionReceived";
}
