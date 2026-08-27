using Iridium.Protocol;
using Iridium.Server.Domain;

namespace Iridium.Server.Api;

public static class DirectMessageMapper
{
    public static DirectMessageDto ToDto(DirectMessage message) => new(
        message.Id,
        message.ConversationId,
        new MessageAuthorDto(message.AuthorAccountId, message.AuthorAccount.Username, message.AuthorAccount.DisplayName),
        message.IsDeleted ? string.Empty : message.Content,
        message.CreatedAt,
        message.EditedAt,
        message.IsDeleted,
        message.ReplyToMessage is null
            ? null
            : new MessageReplyDto(
                message.ReplyToMessage.Id,
                message.ReplyToMessage.AuthorAccountId,
                message.ReplyToMessage.AuthorAccount.DisplayName,
                message.ReplyToMessage.IsDeleted ? null : Excerpt(message.ReplyToMessage.Content),
                message.ReplyToMessage.IsDeleted,
                message.ReplyToMessage.IsDeleted ? null : ChannelMessageMapper.AttachmentSummary(message.ReplyToMessage.Attachments)),
        message.ClientMessageId,
        Attachments: message.IsDeleted ? [] : message.Attachments.Select(ChannelMessageMapper.ToAttachment).ToArray(),
        Kind: message.Kind,
        RelatedCallId: message.RelatedCallId,
        Forwarded: message.IsDeleted ? null : ChannelMessageMapper.ToForwarded(message.ForwardedMessageSnapshot));

    public static DirectConversationDto ConversationToDto(
        DirectConversation conversation,
        Guid accountId,
        PublicPresence otherPresence,
        DateTimeOffset? lastMessageAt = null,
        int unreadCount = 0)
    {
        var other = conversation.ParticipantAAccountId == accountId
            ? conversation.ParticipantBAccount
            : conversation.ParticipantAAccount;
        return new DirectConversationDto(
            conversation.Id,
            new DirectParticipantDto(other.Id, other.Username, other.DisplayName, other.Pronouns, other.Description, otherPresence),
            conversation.CreatedAt,
            lastMessageAt ?? conversation.Messages.OrderByDescending(value => value.CreatedAt).FirstOrDefault()?.CreatedAt,
            unreadCount);
    }

    private static string Excerpt(string content)
        => content;
}
