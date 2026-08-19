namespace Iridium.Server.Domain;

public enum FriendshipState
{
    Pending,
    Accepted
}

public sealed class Friendship
{
    public Guid Id { get; set; }
    public Guid RequesterAccountId { get; set; }
    public Guid AddresseeAccountId { get; set; }
    public FriendshipState Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public required NodeAccount RequesterAccount { get; set; }
    public required NodeAccount AddresseeAccount { get; set; }
}
