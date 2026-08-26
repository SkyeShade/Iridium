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
    public Guid InstanceId { get; } = Guid.NewGuid();
    public MobilePanel Current { get; private set; } = MobilePanel.Navigation;
    public long Revision { get; private set; }
    public event Action<MobilePanelTransition>? Changed;

    public MobilePanelTransition ShowNavigation(string source = "ShowNavigation") => Set(MobilePanel.Navigation, source);
    public MobilePanelTransition ShowConversation(string source = "ShowConversation") => Set(MobilePanel.Conversation, source);
    public MobilePanelTransition ShowContext(string source = "ShowContext") => Set(MobilePanel.Context, source);
    public MobilePanelTransition CloseContext(string source = "CloseContext") => Set(MobilePanel.Conversation, source);
    public MobilePanelTransition Reset(string source = "Reset") => Set(MobilePanel.Navigation, source);

    private MobilePanelTransition Set(MobilePanel panel, string source)
    {
        var transition = new MobilePanelTransition(InstanceId, Current, panel, ++Revision, source);
        Current = panel;
        Changed?.Invoke(transition);
        return transition;
    }
}

public sealed record MobilePanelTransition(Guid InstanceId, MobilePanel Previous, MobilePanel Current,
    long Revision, string Source);
