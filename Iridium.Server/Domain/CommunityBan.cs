namespace Iridium.Server.Domain;

public sealed class CommunityBan
{
    public Guid CommunityId { get; set; }
    public Guid AccountId { get; set; }
    public Guid BannedByAccountId { get; set; }
    public DateTimeOffset BannedAt { get; set; }
    public string? Reason { get; set; }
    public required Community Community { get; set; }
    public required NodeAccount Account { get; set; }
    public required NodeAccount BannedByAccount { get; set; }
}
