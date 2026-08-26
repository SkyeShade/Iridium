namespace Iridium.Server.Domain;

public sealed class ChannelMessage
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid ChannelId { get; set; }
    public Guid AuthorAccountId { get; set; }
    public Guid? ClientMessageId { get; set; }
    public string? AuthorDisplayNameSnapshot { get; set; }
    public string? AuthorAvatarObjectKeySnapshot { get; set; }
    public string? AuthorAvatarContentTypeSnapshot { get; set; }
    public int? AuthorAvatarWidthSnapshot { get; set; }
    public int? AuthorAvatarHeightSnapshot { get; set; }
    public double? AuthorAvatarCropXSnapshot { get; set; }
    public double? AuthorAvatarCropYSnapshot { get; set; }
    public double? AuthorAvatarZoomSnapshot { get; set; }
    public long? AuthorAvatarRevisionSnapshot { get; set; }
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
    public ICollection<Attachment> Attachments { get; set; } = [];
}
