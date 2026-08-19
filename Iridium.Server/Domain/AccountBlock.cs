namespace Iridium.Server.Domain;

public sealed class AccountBlock
{
    public Guid BlockingAccountId { get; set; }
    public Guid BlockedAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required NodeAccount BlockingAccount { get; set; }
    public required NodeAccount BlockedAccount { get; set; }
}
