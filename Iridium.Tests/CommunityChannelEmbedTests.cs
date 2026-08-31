using Iridium.Protocol;
using Iridium.Server.Domain;

namespace Iridium.Tests;

public sealed class CommunityChannelEmbedTests
{
    [Theory]
    [InlineData("https://docs.google.com/document/d/abcdefghij/edit")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/edit?usp=sharing")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/view")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/preview")]
    [InlineData("https://docs.google.com/document/d/abc_DEF-123456/edit?tab=t.0#heading=h.a")]
    public void GoogleDocsUrlsAreCanonicalized(string input)
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleDocs(input, out var result));
        Assert.NotNull(result);
        Assert.Equal($"https://docs.google.com/document/d/{result.DocumentId}/view", result.OpenUrl);
        Assert.Equal($"https://docs.google.com/document/d/{result.DocumentId}/preview", result.FrameUrl);
        Assert.DoesNotContain('?', result.FrameUrl);
        Assert.DoesNotContain('#', result.FrameUrl);
        Assert.Null(result.PublishedUrl);
        Assert.Equal($"https://docs.google.com/document/d/{result.DocumentId}/export?format=html",
            result.AnonymousExportUrl);
        Assert.Equal(result.AnonymousExportUrl, result.FetchUrl);
        Assert.Equal(GoogleDocsInputKind.ShareLink, result.InputKind);
        Assert.Equal(GoogleDocsFetchMode.AnonymousExport, result.FetchMode);
    }

    [Fact]
    public void LegacyPublishedGoogleDocsUrlRetainsFetchUrlAndExternalDocumentUrl()
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleDocs(
            "https://docs.google.com/document/d/abcdefghij/pub?embedded=true", out var result));
        Assert.NotNull(result);
        Assert.Equal("https://docs.google.com/document/d/abcdefghij/pub", result.PublishedUrl);
        Assert.Equal(result.PublishedUrl, result.CanonicalUrl);
        Assert.Equal("https://docs.google.com/document/d/abcdefghij/view", result.OpenUrl);
        Assert.Equal(GoogleDocsInputKind.PublishedLink, result.InputKind);
        Assert.Equal(GoogleDocsFetchMode.PublishedHtml, result.FetchMode);
    }

    [Fact]
    public void PublishedGoogleDocsUrlRetainsOnlyCanonicalPublicationIdentity()
    {
        const string input = "https://docs.google.com/document/d/e/2PACX-abcdefghij_123/pub?embedded=true#ignored";
        Assert.True(CommunityChannelEmbeds.TryGoogleDocs(input, out var result));
        Assert.NotNull(result);
        Assert.Equal("2PACX-abcdefghij_123", result.DocumentId);
        Assert.Equal("https://docs.google.com/document/d/e/2PACX-abcdefghij_123/pub", result.PublishedUrl);
        Assert.Equal(result.PublishedUrl, result.OpenUrl);
    }

    [Theory]
    [InlineData("http://docs.google.com/document/d/abcdefghij/edit")]
    [InlineData("https://evil.example/document/d/abcdefghij/edit")]
    [InlineData("https://docs.google.com.evil.example/document/d/abcdefghij/edit")]
    [InlineData("https://docs.google.com@evil.example/document/d/abcdefghij/edit")]
    [InlineData("https://docs.google.com/document/d/abcdefghij%2Flocalhost/edit")]
    [InlineData("https://docs.google.com/spreadsheets/d/abcdefghij/edit")]
    [InlineData("https://docs.google.com/document/d/short/edit")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/export")]
    [InlineData("https://docs.google.com/document/d/e/short/pub")]
    [InlineData("https://docs.google.com/document/d/e/2PACX-abcdefghij/pub/extra")]
    [InlineData("not a url")]
    public void UnsafeOrMalformedGoogleDocsUrlsAreRejected(string input) =>
        Assert.False(CommunityChannelEmbeds.TryGoogleDocs(input, out _));

    [Fact]
    public void ExistingChannelDefaultsToNoEmbed()
    {
        var channel = new CommunityChannel { Name = "general", Community = null! };
        Assert.Null(channel.EmbedProvider);
        Assert.Null(channel.EmbedUrl);
    }

    [Fact]
    public void ChannelUiKeepsDocumentInMessageViewportAndUsesIndependentJumpState()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var channel = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelView.razor"));
        var list = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageList.razor"));
        var chat = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "chat.js"));
        var styles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelView.razor.css"));
        var renderer = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedDocumentBlock.razor"));
        var inlineRenderer = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedDocumentInline.razor"));
        var csp = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "index.html"));

        Assert.Contains("<BeforeMessages>", channel);
        Assert.True(channel.IndexOf("data-channel-document-anchor", StringComparison.Ordinal) <
                    channel.IndexOf("channel-document-separator", StringComparison.Ordinal));
        Assert.Contains("HasLeadingContent=\"@HasDocumentEmbed\"", channel);
        Assert.Contains("class=\"channel-jump-document\"", channel);
        Assert.Contains("class=\"channel-jump-latest\"", channel);
        Assert.DoesNotContain("ShowJumpToDocument || ShowJumpToLatest", channel);
        Assert.Contains("_nearDocumentTop = true", channel);
        Assert.Contains("This document cannot be displayed directly in Iridium", channel);
        Assert.Contains("ChannelEmbedDocumentStatus.TooLarge", channel);
        Assert.Contains("ChannelEmbedDocumentStatus.ParseFailure", channel);
        Assert.Contains("ChannelEmbedDocumentStatus.Timeout", channel);
        Assert.Contains("data-message-history-start", list);
        Assert.Contains("scrollMessageLeadingContent", list);
        Assert.Contains("scrollIntoView({ behavior: \"smooth\", block: \"start\" })", chat);
        Assert.Contains("isNearDocumentTop", chat);
        Assert.Contains("documentAnchor?.getBoundingClientRect().top", chat);
        Assert.Contains("nearHistoryStart", chat);
        Assert.Contains("<EmbeddedDocumentView", channel);
        Assert.DoesNotContain("MarkupString", channel);
        Assert.DoesNotContain("<iframe", channel);
        Assert.Contains("height:auto;overflow:visible", styles);
        Assert.Contains(".embedded-document", styles);
        Assert.Contains("height:auto", styles);
        Assert.Contains("embedded-document-table", styles);
        Assert.Contains("EmbeddedDocumentHeadingDto", renderer);
        Assert.Contains("EmbeddedDocumentListDto", renderer);
        Assert.Contains("EmbeddedDocumentTableDto", renderer);
        Assert.Contains("EmbeddedDocumentImageDto", renderer);
        Assert.Contains(".channel-jump-document{top:", styles);
        Assert.Contains(".channel-jump-latest{bottom:", styles);
        Assert.Contains("line-height:1.48", styles);
        Assert.Contains("margin:0 0 .48rem", styles);
        Assert.Contains("embedded-document-image.align-start", styles);
        Assert.Contains("document-paragraph", renderer);
        Assert.Contains("document-heading", renderer);
        Assert.Contains("document-list", renderer);
        Assert.Contains("document-spacer", renderer);
        Assert.Contains("line-height:1.34", styles);
        Assert.Contains("margin:0 0 .3rem", styles);
        Assert.Contains("--document-spacer-lines", styles);
        Assert.Contains("doc-text-color-red", inlineRenderer);
        Assert.Contains("doc-text-color-purple", inlineRenderer);
        Assert.Contains("--document-text-red", styles);
        Assert.Contains("color:var(--document-text-blue)", styles);
        Assert.DoesNotContain("style=\"color:", inlineRenderer);
        Assert.DoesNotContain("invert(", styles);
        Assert.DoesNotContain("overflow-y:auto", styles);
        Assert.DoesNotContain("channel-document iframe", styles);
        Assert.DoesNotContain("iframe.contentDocument", chat);
        Assert.DoesNotContain("iframe.contentWindow", chat);
        Assert.Contains("frame-src https://www.youtube-nocookie.com", csp);
        Assert.DoesNotContain("https://docs.google.com", csp);
        Assert.DoesNotContain("frame-src *", csp);
    }

    [Fact]
    public void TextChannelSettingsUseCanonicalEmbedValidationAndCanClearTheDraft()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var fullSettings = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "CommunityPermissionEditor.razor"));
        var compactSettings = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelSettingsDialog.razor"));
        var endpoint = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Api", "CommunityStructureEndpoints.cs"));
        var canonicalSwitch = File.ReadAllText(Path.Combine(root, "Iridium.UI", "SettingsSwitch.razor"));
        var canonicalSwitchStyles = File.ReadAllText(Path.Combine(root, "Iridium.UI", "SettingsSwitch.razor.css"));

        Assert.Contains("Channel.Kind == CommunityChannelKind.Text", fullSettings);
        Assert.Contains("<SettingsSwitch", fullSettings);
        Assert.Contains("<SettingsSwitch", compactSettings);
        Assert.Contains("type=\"checkbox\"", canonicalSwitch);
        Assert.Contains("input:checked+span::after", canonicalSwitchStyles);
        Assert.Contains("translateX", canonicalSwitchStyles);
        Assert.Contains("input:focus-visible+span", canonicalSwitchStyles);
        Assert.Contains("CommunityChannelEmbeds.TryResolveContent", fullSettings);
        Assert.Contains("CommunityChannelEmbeds.TryResolveContent", compactSettings);
        Assert.DoesNotContain("<span>Provider</span>", fullSettings);
        Assert.DoesNotContain("field-label\">Provider", compactSettings);
        Assert.Contains("DetectedEmbed", fullSettings);
        Assert.Contains("DetectedEmbed", compactSettings);
        Assert.Contains("embed?.Provider, embed?.OpenUrl", fullSettings);
        Assert.Contains("embed?.Provider, embed?.OpenUrl", compactSettings);
        Assert.Contains("TryResolveContent(embed.Url", endpoint);
        Assert.Contains("channel.EmbedUrl = content.OpenUrl", endpoint);
        Assert.Contains("channel.EmbedProvider = null", endpoint);
        Assert.Contains("Only Text Channels can embed documents", endpoint);
    }
}
