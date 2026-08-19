namespace Iridium.Server.Domain;

public sealed class CommunityChannelReadState
{
    public Guid CommunityId { get; set; }
    public Guid ChannelId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset LastReadAt { get; set; }
    public required CommunityChannel Channel { get; set; }
    public required NodeAccount Account { get; set; }
}
