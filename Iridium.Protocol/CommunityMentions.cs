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

public sealed record CommunityMentionReceivedEvent(
    Guid CommunityId,
    Guid ChannelId,
    Guid MessageId,
    Guid AuthorAccountId);

public static class CommunityMentionHubContract
{
    public const string Received = "CommunityMentionReceived";
}
