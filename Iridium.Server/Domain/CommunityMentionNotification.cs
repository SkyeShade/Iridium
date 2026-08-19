namespace Iridium.Server.Domain;

public sealed class CommunityMentionNotification
{
    public Guid MessageId { get; set; }
    public Guid AccountId { get; set; }
    public Guid CommunityId { get; set; }
    public Guid ChannelId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public required ChannelMessage Message { get; set; }
    public required NodeAccount Account { get; set; }
}
