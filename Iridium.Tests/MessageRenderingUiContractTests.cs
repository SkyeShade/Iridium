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

        Assert.Contains("contenteditable=\"true\"", composer);
        Assert.Contains("markdown-marker", previewCss);
        Assert.DoesNotContain("font-family:", previewCss);
        Assert.DoesNotContain("font-size:.92em", previewCss);
        Assert.Contains(".markdown-italic{font-style:oblique 10deg}", previewCss);
        Assert.DoesNotContain("text-decoration-style:dotted", previewCss);
        Assert.DoesNotContain("font-weight:700", previewCss);
        Assert.DoesNotContain("display:none", previewCss);
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

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
}
