namespace Iridium.Web.Models;

public readonly record struct MessageViewportState(
    bool IsPinnedToLatest,
    bool ShouldShowJumpToLatest);
