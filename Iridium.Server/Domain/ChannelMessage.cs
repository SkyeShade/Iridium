namespace Iridium.Server.Domain;

public sealed class ChannelMessage
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid ChannelId { get; set; }
    public Guid AuthorAccountId { get; set; }
    public required string Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public string? MentionsJson { get; set; }
    public required CommunityChannel Channel { get; set; }
    public required NodeAccount AuthorAccount { get; set; }
    public ChannelMessage? ReplyToMessage { get; set; }
    public ICollection<ChannelMessage> Replies { get; set; } = [];
}
