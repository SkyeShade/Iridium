namespace Iridium.Server.Domain;

public sealed class DirectConversation
{
    public Guid Id { get; set; }
    public Guid ParticipantAAccountId { get; set; }
    public Guid ParticipantBAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required NodeAccount ParticipantAAccount { get; set; }
    public required NodeAccount ParticipantBAccount { get; set; }
    public ICollection<DirectMessage> Messages { get; set; } = [];
    public ICollection<DirectConversationState> ParticipantStates { get; set; } = [];
}
