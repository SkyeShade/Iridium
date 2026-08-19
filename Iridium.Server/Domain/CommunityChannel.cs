namespace Iridium.Server.Domain;

public sealed class CommunityChannel
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid? CategoryId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required Community Community { get; set; }
    public CommunityCategory? Category { get; set; }
    public ICollection<ChannelMessage> Messages { get; set; } = [];
}
