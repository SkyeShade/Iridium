namespace Iridium.UI;

public enum MobileDirectCallPresentation
{
    Collapsed,
    Expanded
}

public sealed class MobileDirectCallPresentationState
{
    public MobileDirectCallPresentation Current { get; private set; } = MobileDirectCallPresentation.Collapsed;
    public Guid? ConversationId { get; private set; }
    public bool IsExpanded => Current == MobileDirectCallPresentation.Expanded;
    public event Action? Changed;

    public bool IsExpandedFor(Guid conversationId) =>
        IsExpanded && ConversationId == conversationId;

    public void Expand(Guid conversationId)
    {
        if (IsExpandedFor(conversationId)) return;
        Current = MobileDirectCallPresentation.Expanded;
        ConversationId = conversationId;
        Changed?.Invoke();
    }

    public void Collapse()
    {
        if (!IsExpanded && ConversationId is null) return;
        Current = MobileDirectCallPresentation.Collapsed;
        ConversationId = null;
        Changed?.Invoke();
    }

    public void Reset() => Collapse();
}
