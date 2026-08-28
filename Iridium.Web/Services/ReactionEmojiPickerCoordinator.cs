using Iridium.Protocol;
using Microsoft.AspNetCore.Components;

namespace Iridium.Web.Services;

public sealed record ReactionEmojiPickerState(
    Guid MessageId,
    CommunityDto? Community,
    Guid AccountId,
    bool AllowExternalEmoji,
    ElementReference Anchor,
    Func<EmojiSelection, Task> OnSelected)
{
    public Guid InstanceId { get; init; } = Guid.NewGuid();
}

public sealed class ReactionEmojiPickerCoordinator
{
    public ReactionEmojiPickerState? Current { get; private set; }
    public event Action? Changed;

    public void Open(ReactionEmojiPickerState state)
    {
        Current = state;
        Changed?.Invoke();
    }

    public void Close(Guid? messageId = null)
    {
        if (Current is null || messageId is not null && Current.MessageId != messageId) return;
        Current = null;
        Changed?.Invoke();
    }
}
