namespace Iridium.Protocol;

public static class ChatHubContract
{
    public const string JoinChannel = "JoinChannel";
    public const string LeaveChannel = "LeaveChannel";
    public const string SendMessage = "SendMessage";
    public const string EditMessage = "EditMessage";
    public const string DeleteMessage = "DeleteMessage";
    public const string MessageCreated = "MessageCreated";
    public const string MessageUpdated = "MessageUpdated";
    public const string MessageDeleted = "MessageDeleted";
}

public static class FriendshipHubContract
{
    public const string RequestReceived = "FriendRequestReceived";
    public const string RequestAccepted = "FriendRequestAccepted";
    public const string RequestDeclined = "FriendRequestDeclined";
    public const string FriendshipRemoved = "FriendshipRemoved";
}

public sealed record FriendshipChangedEvent(Guid FriendshipId);

public sealed record MessageAuthorDto(Guid AccountId, string Username, string DisplayName);

public enum MessageDeliveryState
{
    Confirmed,
    Pending,
    Failed
}

public sealed record MessageReplyDto(
    Guid MessageId,
    Guid AuthorAccountId,
    string AuthorDisplayName,
    string? Excerpt,
    bool IsDeleted);

public sealed record ChannelMessageDto(
    Guid Id,
    Guid CommunityId,
    Guid ChannelId,
    MessageAuthorDto Author,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool IsDeleted,
    MessageReplyDto? ReplyTo,
    IReadOnlyList<CommunityMentionDto>? Mentions = null,
    Guid? ClientMessageId = null,
    MessageDeliveryState DeliveryState = MessageDeliveryState.Confirmed,
    string? DeliveryError = null,
    bool CanRetry = false,
    IReadOnlyList<AttachmentDto>? Attachments = null);

public sealed record SendChannelMessageRequest(
    string Content,
    Guid? ReplyToMessageId,
    IReadOnlyList<CommunityMentionInput>? Mentions = null,
    Guid? ClientMessageId = null,
    IReadOnlyList<Guid>? AttachmentIds = null);
public sealed record EditChannelMessageRequest(string Content);
public sealed record ChannelMessageDeletedEvent(Guid CommunityId, Guid ChannelId, Guid MessageId, DateTimeOffset DeletedAt);
