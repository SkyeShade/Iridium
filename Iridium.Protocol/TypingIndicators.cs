namespace Iridium.Protocol;

public static class TypingHubContract
{
    public const string SetActivity = "SetTypingActivity";
    public const string Changed = "TypingActivityChanged";
}

public enum TypingConversationKind
{
    CommunityChannel = 0,
    DirectConversation = 1
}

public sealed record TypingConversationDto(
    TypingConversationKind Kind,
    Guid ConversationId,
    Guid? CommunityId = null);

public sealed record SetTypingActivityRequest(TypingConversationDto Conversation, Guid SessionId, bool IsTyping);

public sealed record TypingActivityEvent(
    TypingConversationDto Conversation,
    Guid AccountId,
    Guid SessionId,
    string DisplayName,
    bool IsTyping,
    DateTimeOffset OccurredAt);
