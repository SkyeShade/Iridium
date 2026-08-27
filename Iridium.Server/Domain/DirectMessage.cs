using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class DirectMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid AuthorAccountId { get; set; }
    public Guid? ClientMessageId { get; set; }
    public MessageKind Kind { get; set; } = MessageKind.User;
    public Guid? RelatedCallId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public Guid? ForwardedMessageSnapshotId { get; set; }
    public required DirectConversation Conversation { get; set; }
    public required NodeAccount AuthorAccount { get; set; }
    public DirectMessage? ReplyToMessage { get; set; }
    public ForwardedMessageSnapshot? ForwardedMessageSnapshot { get; set; }
    public ICollection<DirectMessage> Replies { get; set; } = [];
    public ICollection<Attachment> Attachments { get; set; } = [];
}
