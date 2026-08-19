namespace Iridium.Server.Domain;

public sealed class CommunityInvite
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public required string TokenHash { get; set; }
    public required string CodePrefix { get; set; }
    public Guid CreatedByAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public bool Revoked { get; set; }
    public required Community Community { get; set; }
    public required NodeAccount CreatedByAccount { get; set; }
}
