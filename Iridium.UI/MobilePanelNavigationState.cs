namespace Iridium.UI;

public enum MobilePanel
{
    Navigation,
    Conversation,
    Context
}

/// <summary>Presentation-only navigation state; conversation data remains in the existing sessions.</summary>
public sealed class MobilePanelNavigationState
{
    public MobilePanel Current { get; private set; } = MobilePanel.Navigation;

    public void ShowNavigation() => Current = MobilePanel.Navigation;
    public void ShowConversation() => Current = MobilePanel.Conversation;
    public void ShowContext() => Current = MobilePanel.Context;
    public void CloseContext() => Current = MobilePanel.Conversation;
    public void Reset() => Current = MobilePanel.Navigation;
}
