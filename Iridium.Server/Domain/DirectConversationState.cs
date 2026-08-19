namespace Iridium.Server.Domain;

public sealed class DirectConversationState
{
    public Guid ConversationId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    public required DirectConversation Conversation { get; set; }
    public required NodeAccount Account { get; set; }
}
