namespace Iridium.Tests;

public sealed class MessageRenderingUiContractTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void FriendResultUsesSharedCompactAvatarAndResponsiveEllipsisLayout()
    {
        var view = Source("Iridium.Web", "Components", "AddFriendView.razor");
        var css = Source("Iridium.Web", "Components", "AddFriendView.razor.css");
        var avatar = Source("Iridium.UI", "ProfileAvatar.razor");

        Assert.Contains("<ProfileAvatar AccountId=\"profile.AccountId\"", view);
        Assert.Contains("Size=\"medium\"", view);
        Assert.Contains("AvatarCroppedPreview", avatar);
        Assert.Contains("avatar-initial", avatar);
        Assert.Contains("grid-template-columns:2.5rem minmax(0,1fr) auto", css);
        Assert.Contains("text-overflow:ellipsis", css);
        Assert.Contains("@media(max-width:470px)", css);
    }

    [Fact]
    public void SameNodeFriendAutocompleteReusesCompactRowsAndHasStaleAndKeyboardProtection()
    {
        var view = Source("Iridium.Web", "Components", "AddFriendView.razor");
        var css = Source("Iridium.Web", "Components", "AddFriendView.razor.css");
        var javascript = Source("Iridium.Web", "wwwroot", "js", "chat.js");

        Assert.Contains("Task.Delay(225", view);
        Assert.Contains("generation == _queryGeneration", view);
        Assert.Contains("!query.Contains('@')", view);
        Assert.Contains("HandleFriendSearchKeyAsync", view);
        Assert.Contains("ArrowDown", view);
        Assert.Contains("wireFriendAutocomplete", javascript);
        Assert.Contains("document.addEventListener(\"pointerdown\", outside)", javascript);
        Assert.Contains("resolved-card suggestion-card", view);
        Assert.Contains("<ProfileAvatar AccountId=\"suggestion.AccountId\"", view);
        Assert.Contains(".suggestion-card", css);
    }

    [Fact]
    public void ReplyPreviewUsesSharedAvatarRoleColorAndMarkdownRendererWithClamp()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var css = Source("Iridium.Web", "Components", "MessageRow.razor.css");

        Assert.Contains("AccountId=\"reply.AuthorAccountId\"", row);
        Assert.Contains("CommunityRolePresentation.MemberColor", row);
        Assert.Contains("<MentionedMessageContent", row);
        Assert.Contains("Compact=\"true\"", row);
        Assert.Contains("Deleted message", row);
        Assert.DoesNotContain("Original message deleted", row);
        Assert.Contains("text-overflow:ellipsis", css);
    }

    [Fact]
    public void ComposerOverlayKeepsRawGlyphMetricsAndVisibleMarkers()
    {
        var previewCss = Source("Iridium.Web", "Components", "ComposerMarkdownPreview.razor.css");
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");
        var composerCss = Source("Iridium.Web", "Components", "MessageComposer.razor.css");
        var messageCss = Source("Iridium.Web", "Components", "MessageContentNodeView.razor.css");
        var appCss = Source("Iridium.Web", "wwwroot", "css", "app.css");

        Assert.Contains("contenteditable=\"true\"", composer);
        Assert.Contains("markdown-marker", previewCss);
        Assert.DoesNotContain("font-family:", previewCss);
        Assert.DoesNotContain("font-size:.92em", previewCss);
        Assert.Contains(".markdown-italic{font-style:oblique 10deg}", previewCss);
        Assert.DoesNotContain("text-decoration-style:dotted", previewCss);
        Assert.DoesNotContain("font-weight:700", previewCss);
        Assert.Contains("font-weight:inherit", previewCss);
        Assert.Contains("-webkit-text-stroke:.025em currentColor", previewCss);
        Assert.Contains("paint-order:stroke fill", previewCss);
        Assert.Contains("--chat-strong-weight: 700", appCss);
        Assert.Contains("strong{font-weight:var(--chat-strong-weight)}", messageCss);
        Assert.Contains("composer-highlight composer-text-geometry", composer);
        Assert.Contains("font-weight:var(--chat-text-weight)", composerCss);
        Assert.DoesNotContain("display:none", previewCss);
    }

    [Fact]
    public void ClipboardFilesReuseThePickerAttachmentPipelineWithoutChangingMessageSource()
    {
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");
        var javascript = Source("Iridium.Web", "wwwroot", "js", "chat.js");
        var pasteStart = javascript.IndexOf("const paste = async event =>", StringComparison.Ordinal);
        var pasteEnd = javascript.IndexOf("const scroll = () =>", pasteStart, StringComparison.Ordinal);
        Assert.True(pasteStart >= 0 && pasteEnd > pasteStart);
        var paste = javascript[pasteStart..pasteEnd];

        Assert.Contains("composerClipboardFiles(event.clipboardData)", paste);
        Assert.Contains("stageComposerFiles(composerRoot, files)", paste);
        Assert.Contains("PrepareAttachmentPasteAsync", paste);
        Assert.Contains("stagedFiles.dispatch()", paste);
        Assert.True(paste.IndexOf("return;", StringComparison.Ordinal) <
                    paste.IndexOf("getData(\"text/plain\")", StringComparison.Ordinal));
        Assert.DoesNotContain("ComposerDocumentChangedAsync", paste[..paste.IndexOf("return;", StringComparison.Ordinal)]);

        Assert.Contains("OnChange=\"FilesSelectedAsync\"", composer);
        Assert.Contains("Attachments.AddAsync(key, args.GetMultipleFiles(100), metadata)", composer);
        Assert.Contains("_focusCaretAfterRender = _pickerCaret ?? _content.Length", composer);
    }

    [Fact]
    public void StandardHistoryQueriesExcludeRowsButKeepReplyJoins()
    {
        var channels = Source("Iridium.Server", "Api", "MessageEndpoints.cs");
        var direct = Source("Iridium.Server", "Api", "DirectMessageEndpoints.cs");
        Assert.Contains("value.ChannelId == channelId && !value.IsDeleted", channels);
        Assert.Contains("value.ConversationId == conversationId && !value.IsDeleted", direct);
        Assert.Contains("Include(value => value.ReplyToMessage)", channels);
        Assert.Contains("Include(value => value.ReplyToMessage)", direct);
    }

    [Fact]
    public void ComposerAndMessageBodyShareResponsiveColumnAndTypographyTokens()
    {
        var app = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var row = Source("Iridium.Web", "Components", "MessageRow.razor.css");
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor.css");

        Assert.Contains("--chat-content-column: 4.15rem", app);
        Assert.Contains("--chat-content-column: 3.5rem", app);
        Assert.Contains("--chat-text-size: 1rem", app);
        Assert.Contains("--chat-line-height: 1.35", app);
        Assert.Contains("grid-template-columns: var(--chat-content-column)", row);
        Assert.Contains("font-size:var(--chat-text-size)", row);
        Assert.Contains("grid-template-columns:calc(var(--chat-content-column)", composer);
        Assert.Contains("font-size:var(--chat-text-size)", composer);
        Assert.DoesNotContain("font-size: .9rem", row);
        Assert.DoesNotContain("transform:scale", row);
        Assert.DoesNotContain("transform:scale", composer);
    }

    [Fact]
    public void StandardAndCustomInlineEmojiUseOneTextRelativeMetric()
    {
        var app = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var mentioned = Source("Iridium.Web", "Components", "MentionedMessageContent.razor.css");

        Assert.Contains("--chat-inline-emoji-size: 1.3em", app);
        Assert.Contains("width: var(--chat-inline-emoji-size)", app);
        Assert.Contains(".inline-emoji-token > .standard-emoji-artwork", app);
        Assert.Contains(".inline-emoji-token > .community-emoji", app);
        Assert.Contains("object-fit: contain", app);
        Assert.Contains(".message-text.emoji-only-message{font-size:3em", mentioned);
        Assert.Contains("width:1em!important", mentioned);
    }

    [Fact]
    public void YouTubeEmbedIsLazyTrustedCappedAndLeavesMessageLinkRenderingIntact()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var card = Source("Iridium.Web", "Components", "YouTubeEmbedCard.razor");
        var resolver = Source("Iridium.Client.Core", "ExternalEmbeds.cs");
        var content = Source("Iridium.Web", "Components", "MessageContentNodeView.razor");
        var index = Source("Iridium.Web", "wwwroot", "index.html");

        Assert.Contains("<MessageExternalEmbeds Content=\"@Message.Content\"", row);
        Assert.Contains("@if (_activated)", card);
        Assert.Contains("youtube-nocookie.com/embed", resolver);
        Assert.Contains("MaximumEmbedsPerMessage = 3", resolver);
        Assert.Contains("i.ytimg.com/vi", resolver);
        Assert.DoesNotContain("autoplay", card, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", content);
        Assert.Contains("frame-src https://www.youtube-nocookie.com", index);
    }

    [Fact]
    public void ValidatedMp4UsesNativeMetadataOnlyVideoAndCacheKeepsMetadataOnly()
    {
        var attachments = Source("Iridium.Web", "Components", "MessageAttachments.razor");
        var video = Source("Iridium.Web", "Components", "PostedVideo.razor");
        var cache = Source("Iridium.Web", "wwwroot", "js", "messageHistoryCache.js");
        var endpoint = Source("Iridium.Server", "Api", "AttachmentEndpoints.cs");
        var chat = Source("Iridium.Web", "wwwroot", "js", "chat.js");
        var lazy = Source("Iridium.Web", "wwwroot", "js", "mediaEmbeds.js");

        Assert.Contains("attachment.ContentType.Equals(\"video/mp4\"", attachments);
        Assert.Contains("<video src=\"@_source\" controls preload=\"metadata\" playsinline", video);
        Assert.DoesNotContain("autoplay", video, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aspect-ratio:var(--video-aspect)", Source("Iridium.Web", "Components", "PostedVideo.razor.css"));
        Assert.Contains("metadata: attachment", cache);
        Assert.DoesNotContain("arrayBuffer", cache, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enableRangeProcessing: true", endpoint);
        Assert.Contains("CanAccessAsync(attachment, accountId", endpoint);
        Assert.Contains("video.videoWidth", chat);
        Assert.Contains("video.videoHeight", chat);
        Assert.Contains("URL.revokeObjectURL(objectUrl)", chat);
        Assert.Contains("IntersectionObserver", lazy);
        Assert.Contains("VideoBecameVisibleAsync", video);
    }

    [Fact]
    public void TextChannelUnreadMarkerUsesAuthoritativeCountWithoutReplacingMentions()
    {
        var row = Source("Iridium.UI", "ChannelRow.razor");
        var css = Source("Iridium.UI", "ChannelRow.razor.css");
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var session = Source("Iridium.Client.Core", "CommunitySession.cs");

        Assert.Contains("CommunityChannelKind.Text or CommunityChannelKind.Forum", row);
        Assert.Contains("channel-unread-marker", row);
        Assert.Contains("Channel.MentionCount > 0", row);
        Assert.Contains("channel-mention-badge", row);
        Assert.Contains("position:absolute", css);
        Assert.Contains("pointer-events:none", css);
        Assert.Contains("CommunityState.MarkChannelRead(channel.Id)", home);
        Assert.Contains("await CommunityState.LoadAsync(activity.CommunityId)", home);
        Assert.Contains("UnreadCount = 0, MentionCount = 0", session);
    }

    [Theory]
    [InlineData("", 412.5, "412.5px")]
    [InlineData("da-DK", 412.5, "412.5px")]
    [InlineData("da-DK", 9999, "9999px")]
    public void ProfileCardPixelCoordinatesRemainInvariant(string cultureName, double coordinate, string expected)
    {
        var culture = string.IsNullOrEmpty(cultureName)
            ? System.Globalization.CultureInfo.InvariantCulture
            : System.Globalization.CultureInfo.GetCultureInfo(cultureName);
        var currentCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            Assert.Equal(expected, InvariantPixel(coordinate));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = currentCulture; }
    }

    [Fact]
    public void ProfileCardUsesFiniteInvariantFormattingForBothAxes()
    {
        var card = Source("Iridium.Web", "Components", "AnchoredProfileCard.razor");

        Assert.Contains("--profile-x:@CssPixel(X);--profile-y:@CssPixel(Y)", card);
        Assert.Contains("double.IsFinite(value)", card);
        Assert.Contains("value.ToString(CultureInfo.InvariantCulture) + \"px\"", card);
        Assert.Contains(": \"0px\"", card);
        Assert.Equal("0px", InvariantPixel(double.NaN));
        Assert.Equal("0px", InvariantPixel(double.PositiveInfinity));
        Assert.Equal("0px", InvariantPixel(double.NegativeInfinity));
    }

    private static string InvariantPixel(double value) => double.IsFinite(value)
        ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px"
        : "0px";

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
}
