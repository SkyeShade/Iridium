namespace Iridium.Server.Domain;

public sealed class ForwardedMessageSnapshot
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public string? MentionsJson { get; set; }
    public Guid? SourceCommunityId { get; set; }
    public Guid? SourceChannelId { get; set; }
    public Guid? SourceMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<ForwardedMessageAttachment> Attachments { get; set; } = [];
    public ICollection<ChannelMessage> ChannelMessages { get; set; } = [];
    public ICollection<DirectMessage> DirectMessages { get; set; } = [];
}

public sealed class ForwardedMessageAttachment
{
    public Guid ForwardedMessageSnapshotId { get; set; }
    public Guid AttachmentId { get; set; }
    public required ForwardedMessageSnapshot Snapshot { get; set; }
    public required Attachment Attachment { get; set; }
}
