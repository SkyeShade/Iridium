using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.Components;

namespace Iridium.Web.Services;

public sealed record EmojiDetailDescriptor(
    string Glyph,
    string Alias,
    string? ArtworkKey = null,
    CommunityEmojiDto? CustomEmoji = null,
    CommunityDto? SourceCommunity = null);

public sealed record EmojiDetailPopupState(
    EmojiDetailDescriptor Emoji,
    ElementReference? Anchor = null,
    double? ClientX = null,
    double? ClientY = null);

public sealed class EmojiDetailPopupCoordinator(NodeSession session)
{
    public EmojiDetailPopupState? Current { get; private set; }
    public event Action? Changed;

    public void Open(StandardEmoji emoji, ElementReference anchor) =>
        Set(new(new(emoji.Glyph, emoji.Name, emoji.ArtworkKey), anchor));

    public void Open(CommunityEmojiDto emoji, string nameAtSendTime, ElementReference anchor) =>
        Set(new(CustomDescriptor(emoji, nameAtSendTime), anchor));

    public void Open(CommunityEmojiDto emoji, string nameAtSendTime, double clientX, double clientY) =>
        Set(new(CustomDescriptor(emoji, nameAtSendTime), ClientX: clientX, ClientY: clientY));

    public void Close()
    {
        if (Current is null) return;
        Current = null;
        Changed?.Invoke();
    }

    private EmojiDetailDescriptor CustomDescriptor(CommunityEmojiDto emoji, string nameAtSendTime)
    {
        // The membership collection is intentionally the only source of Server identity here.
        var source = session.Communities.FirstOrDefault(value => value.Id == emoji.CommunityId);
        return new(string.Empty, nameAtSendTime, CustomEmoji: emoji, SourceCommunity: source);
    }

    private void Set(EmojiDetailPopupState state)
    {
        Current = state;
        Changed?.Invoke();
    }
}
