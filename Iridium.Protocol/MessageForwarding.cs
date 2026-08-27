namespace Iridium.Protocol;

public enum MessageLocationKind
{
    CommunityChannel = 0,
    DirectConversation = 1
}

public sealed record ForwardMessageSourceDto(
    MessageLocationKind Kind,
    Guid MessageId,
    Guid? CommunityId = null,
    Guid? ChannelId = null,
    Guid? ConversationId = null);

public sealed record ForwardDestinationSelectionDto(
    MessageLocationKind Kind,
    Guid? CommunityId = null,
    Guid? ChannelId = null,
    Guid? ConversationId = null);

public sealed record ForwardMessageRequest(
    ForwardMessageSourceDto Source,
    IReadOnlyList<ForwardDestinationSelectionDto> Destinations,
    string? Note = null);

public sealed record ForwardDestinationDto(
    MessageLocationKind Kind,
    Guid DestinationId,
    string DisplayName,
    string? ContextName,
    Guid? AccountId = null,
    string? Username = null,
    DateTimeOffset? LastActivityAt = null);

public sealed record ForwardSourceReferenceDto(Guid CommunityId, Guid ChannelId, Guid MessageId);

public sealed record ForwardedMessageSnapshotDto(
    Guid Id,
    string Content,
    IReadOnlyList<CommunityMentionDto> Mentions,
    IReadOnlyList<AttachmentDto> Attachments,
    ForwardSourceReferenceDto? Source = null);

public sealed record ForwardMessagesResultDto(
    IReadOnlyList<ChannelMessageDto> ChannelMessages,
    IReadOnlyList<DirectMessageDto> DirectMessages);

public static class MessageForwardingLimits
{
    public const int MaximumDestinations = 5;
}
