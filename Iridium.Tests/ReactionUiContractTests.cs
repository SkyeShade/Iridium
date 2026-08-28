using System.Text.Json;
using System.Runtime.CompilerServices;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class ReactionUiContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void ReactionSummaryRoundTripsWithCachedMessageShape()
    {
        var message = new ChannelMessageDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), "alice", "Alice"), "hello", DateTimeOffset.UtcNow, null, false, null,
            Reactions: [new(new(ReactionEmojiKind.Standard, "👍", "1f44d"), 3, true)]);
        var roundTrip = JsonSerializer.Deserialize<ChannelMessageDto>(JsonSerializer.Serialize(message));
        var reaction = Assert.Single(roundTrip!.Reactions!);
        Assert.Equal(3, reaction.Count);
        Assert.True(reaction.CurrentUserReacted);
        Assert.Equal("1f44d", reaction.Emoji.StandardArtworkKey);
    }

    [Fact]
    public void AccessibleReactionControlsRealtimeAndCachePathsAreWired()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var mobile = Source("Iridium.Web", "Components", "MobileMessageActionSheet.razor");
        var session = Source("Iridium.Client.Core", "ChannelMessagingSession.cs");
        var picker = Source("Iridium.Web", "Components", "EmojiPicker.razor");
        Assert.Contains("class=\"reaction-pill", row);
        Assert.Contains("aria-pressed=\"@reaction.CurrentUserReacted\"", row);
        Assert.Contains("Add Reaction", row);
        Assert.Contains("View Reactions", row);
        Assert.Contains("CanAddReaction", mobile);
        Assert.Contains("ModifierKeys.ShiftPressed", row);
        Assert.Contains("AllowExternalEmoji", picker);
        Assert.Contains("ChatHubContract.MessageReactionChanged", session);
        Assert.Contains("_historyCache.UpsertChannelAsync", session[session.IndexOf(
            "private void ReceiveReactionChanged", StringComparison.Ordinal)..]);
        Assert.Contains("_pendingReactionToggles", session);
    }

    [Fact]
    public void HistoryAggregationIsBatchedAndDetailsAreOnDemand()
    {
        var service = Source("Iridium.Server", "Messages", "MessageReactionService.cs");
        var endpoints = Source("Iridium.Server", "Api", "MessageEndpoints.cs");
        Assert.Contains("ids.Contains(value.MessageId)", service);
        Assert.Contains("GroupBy(value => value.MessageId)", service);
        Assert.DoesNotContain("foreach (var message in messages)", service);
        Assert.Contains("/api/messages/{messageId:guid}/reactions/query", endpoints);
        Assert.Contains("Take(take + 1)", service);
    }

    [Fact]
    public void ReactionPickerUsesLayoutOverlayAndViewportAnchoringWithoutChangingComposerMode()
    {
        var layout = Source("Iridium.Web", "Layout", "MainLayout.razor");
        var overlay = Source("Iridium.Web", "Components", "ReactionEmojiPickerOverlay.razor");
        var picker = Source("Iridium.Web", "Components", "EmojiPicker.razor");
        var pickerCss = Source("Iridium.Web", "Components", "EmojiPicker.razor.css");
        var script = Source("Iridium.Web", "wwwroot", "js", "emojiPicker.js");
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");

        Assert.Contains("<ReactionEmojiPickerOverlay />", layout);
        Assert.Contains("<EmojiPicker", overlay);
        Assert.Contains("AnchoredPopup=\"true\"", overlay);
        Assert.DoesNotContain("<EmojiPicker ", row);
        Assert.Contains("ReactionPickers.Open", row);
        Assert.Contains("AnchoredPopup ? \"anchored-popup\"", picker);
        Assert.Contains("if (AnchoredPopup)", picker);
        Assert.Contains("position:fixed", pickerCss);
        Assert.Contains("visibility:hidden", pickerCss);
        Assert.Contains("anchored-popup.positioned{visibility:visible", pickerCss);
        Assert.Contains("max-width:calc(100vw", pickerCss);
        Assert.Contains("max-height:calc(100dvh", pickerCss);
        Assert.Contains("calculateAnchoredPosition", script);
        Assert.Contains("element.classList.remove(\"positioned\")", script);
        Assert.Contains("Number.isFinite(x)", script);
        Assert.Contains("element.classList.add(\"positioned\")", script);
        Assert.Contains("if (above >= margin)", script);
        Assert.Contains("else if (below + popupRect.height <= viewportHeight - margin)", script);
        Assert.Contains("Math.max(margin, Math.min(viewportWidth - popupRect.width - margin", script);
        Assert.Contains("if (!element.contains(event.target)) close()", script);
        Assert.Contains("document.addEventListener(\"scroll\", closeOnScroll, true)", script);
        Assert.Contains("<EmojiPicker Community=\"Community\"", composer);
        Assert.DoesNotContain("AnchoredPopup", composer);
    }

    [Fact]
    public void DirectMessagesReuseReactionUiRealtimeCacheAndLargerArtworkMetric()
    {
        var directView = Source("Iridium.Web", "Components", "DirectMessageView.razor");
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var details = Source("Iridium.Web", "Components", "ReactionDetailsModal.razor");
        var session = Source("Iridium.Client.Core", "ChannelMessagingSession.cs");
        var artworkCss = Source("Iridium.Web", "Components", "ReactionEmojiArtwork.razor.css");
        var artwork = Source("Iridium.Web", "Components", "ReactionEmojiArtwork.razor");
        var standardCss = Source("Iridium.Web", "Components", "StandardEmojiArtwork.razor.css");
        var directProtocol = Source("Iridium.Protocol", "DirectMessaging.cs");

        Assert.Contains("CanAddReactions=\"true\" CanUseExternalEmoji=\"true\"", directView);
        Assert.Contains("message.Reactions", directView);
        Assert.Contains("IsDirectMessage", row);
        Assert.Contains("ToggleDirectReactionAsync", row);
        Assert.Contains("AddDirectReactionAsync", row);
        Assert.Contains("GetDirectReactionDetailsAsync", details);
        Assert.Contains("DirectMessageHubContract.MessageReactionChanged", session);
        Assert.Contains("_historyCache.UpsertDirectAsync", session[session.IndexOf(
            "private void ReceiveDirectReactionChanged", StringComparison.Ordinal)..]);
        Assert.Contains("IReadOnlyList<ReactionSummaryDto>? Reactions", directProtocol);
        Assert.Contains("width:1.38rem;height:1.38rem;object-fit:contain", artworkCss);
        Assert.Contains("Class=\"reaction-standard-emoji\"", artwork);
        Assert.Contains(".reaction-standard-box{display:inline-grid;width:1.38rem;height:1.38rem", artworkCss);
        Assert.Contains(".reaction-standard-box ::deep .standard-emoji-artwork.reaction-standard-emoji{width:100%;height:100%", artworkCss);
        Assert.Contains(".standard-emoji-artwork{display:inline-grid;place-items:center;width:1em;height:1em", standardCss);
        Assert.DoesNotContain("transform:scale", artworkCss);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string FindRoot([CallerFilePath] string sourceFile = "") =>
        Directory.GetParent(Path.GetDirectoryName(sourceFile)!)!.FullName;
}
