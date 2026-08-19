namespace Iridium.Server.Domain;

public sealed class AccountSession
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public required NodeAccount Account { get; set; }
}
