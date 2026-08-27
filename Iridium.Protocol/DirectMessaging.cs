namespace Iridium.Protocol;

public static class DirectMessageHubContract
{
    public const string JoinConversation = "JoinDirectConversation";
    public const string LeaveConversation = "LeaveDirectConversation";
    public const string SendMessage = "SendDirectMessage";
    public const string EditMessage = "EditDirectMessage";
    public const string DeleteMessage = "DeleteDirectMessage";
    public const string MessageCreated = "DirectMessageCreated";
    public const string MessageUpdated = "DirectMessageUpdated";
    public const string MessageDeleted = "DirectMessageDeleted";
}

public enum MessageKind
{
    User = 0,
    CallStarted = 1
}

public sealed record DirectParticipantDto(
    Guid AccountId,
    string Username,
    string DisplayName,
    string? Pronouns,
    string? Description,
    PublicPresence Presence)
{
    public bool IsOnline => Presence != PublicPresence.Offline;
}

public sealed record DirectConversationDto(
    Guid Id,
    DirectParticipantDto OtherParticipant,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    int UnreadCount);

public sealed record DirectMessageDto(
    Guid Id,
    Guid ConversationId,
    MessageAuthorDto Author,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool IsDeleted,
    MessageReplyDto? ReplyTo,
    Guid? ClientMessageId = null,
    MessageDeliveryState DeliveryState = MessageDeliveryState.Confirmed,
    string? DeliveryError = null,
    bool CanRetry = false,
    IReadOnlyList<AttachmentDto>? Attachments = null,
    MessageKind Kind = MessageKind.User,
    Guid? RelatedCallId = null,
    ForwardedMessageSnapshotDto? Forwarded = null);

public sealed record SendDirectMessageRequest(string Content, Guid? ReplyToMessageId, Guid? ClientMessageId = null,
    IReadOnlyList<Guid>? AttachmentIds = null);
public sealed record EditDirectMessageRequest(string Content);
public sealed record DirectMessageDeletedEvent(Guid ConversationId, Guid MessageId, DateTimeOffset DeletedAt);
