namespace Iridium.Web.Services;

public sealed class MessageMenuCoordinator
{
    public Guid? OpenMessageId { get; private set; }
    public event Action? Changed;

    public void Toggle(Guid messageId)
    {
        OpenMessageId = OpenMessageId == messageId ? null : messageId;
        Changed?.Invoke();
    }

    public void Close(Guid? messageId = null)
    {
        if (OpenMessageId is null || messageId is not null && OpenMessageId != messageId) return;
        OpenMessageId = null;
        Changed?.Invoke();
    }
}
