namespace Iridium.Protocol;

public static class ChatHubContract
{
    public const string JoinChannel = "JoinChannel";
    public const string LeaveChannel = "LeaveChannel";
    public const string SendMessage = "SendMessage";
    public const string EditMessage = "EditMessage";
    public const string DeleteMessage = "DeleteMessage";
    public const string ForwardMessage = "ForwardMessage";
    public const string MessageCreated = "MessageCreated";
    public const string MessageUpdated = "MessageUpdated";
    public const string MessageDeleted = "MessageDeleted";
    public const string AddReaction = "AddReaction";
    public const string RemoveReaction = "RemoveReaction";
    public const string MessageReactionChanged = "MessageReactionChanged";
}

public static class FriendshipHubContract
{
    public const string RequestReceived = "FriendRequestReceived";
    public const string RequestAccepted = "FriendRequestAccepted";
    public const string RequestDeclined = "FriendRequestDeclined";
    public const string FriendshipRemoved = "FriendshipRemoved";
}

public sealed record FriendshipChangedEvent(Guid FriendshipId);

public sealed record MessageAvatarSnapshotDto(
    long Revision,
    double CropX,
    double CropY,
    double Zoom,
    int Width,
    int Height);

public sealed record MessageAuthorDto(
    Guid AccountId,
    string Username,
    string DisplayName,
    Guid? AvatarPresetId = null,
    long AvatarRevision = 0,
    Guid? AvatarSnapshotMessageId = null,
    bool HasHistoricalSnapshot = false,
    MessageAvatarSnapshotDto? AvatarSnapshot = null);

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
    bool IsDeleted,
    string? AttachmentSummary = null,
    Guid? AvatarPresetId = null,
    long AvatarRevision = 0,
    Guid? AvatarSnapshotMessageId = null,
    bool HasHistoricalSnapshot = false,
    MessageAvatarSnapshotDto? AvatarSnapshot = null);

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
    IReadOnlyList<AttachmentDto>? Attachments = null,
    MessageKind Kind = MessageKind.User,
    ForwardedMessageSnapshotDto? Forwarded = null,
    IReadOnlyList<ReactionSummaryDto>? Reactions = null);

public enum ReactionEmojiKind { Standard, Custom }

public sealed record ReactionEmojiDto(
    ReactionEmojiKind Kind,
    string? StandardEmojiValue = null,
    string? StandardArtworkKey = null,
    Guid? CustomEmojiId = null,
    string? CustomEmojiName = null,
    string? CustomEmojiContentType = null,
    bool CustomEmojiAnimated = false,
    int CustomEmojiWidth = 0,
    int CustomEmojiHeight = 0,
    long CustomEmojiRevision = 0,
    bool CustomEmojiAvailable = true);

public sealed record ReactionEmojiRequest(
    ReactionEmojiKind Kind,
    string? StandardEmojiValue = null,
    Guid? CustomEmojiId = null);

public sealed record ReactionSummaryDto(ReactionEmojiDto Emoji, int Count, bool CurrentUserReacted);

public sealed record MessageReactionChangedEvent(
    Guid CommunityId,
    Guid ChannelId,
    Guid MessageId,
    ReactionEmojiDto Emoji,
    int Count,
    Guid AccountId,
    bool Added);

public sealed record ReactionUserDto(
    Guid AccountId,
    string DisplayName,
    Guid? AvatarPresetId = null,
    long AvatarRevision = 0);

public sealed record ReactionDetailsDto(
    ReactionEmojiDto Emoji,
    int Count,
    IReadOnlyList<ReactionUserDto> Users,
    string? NextCursor = null);

public static class MessageReactionLimits
{
    public const int MaximumDistinctPerMessage = 20;
    public const int ReactorPageSize = 50;
    public const int MaximumReactorPageSize = 100;
}

public sealed record SendChannelMessageRequest(
    string Content,
    Guid? ReplyToMessageId,
    IReadOnlyList<CommunityMentionInput>? Mentions = null,
    Guid? ClientMessageId = null,
    IReadOnlyList<Guid>? AttachmentIds = null);
public sealed record EditChannelMessageRequest(string Content);
public sealed record ChannelMessageDeletedEvent(Guid CommunityId, Guid ChannelId, Guid MessageId, DateTimeOffset DeletedAt);
