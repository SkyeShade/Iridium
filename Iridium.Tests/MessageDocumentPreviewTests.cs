using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageDocumentPreviewTests
{
    [Theory]
    [InlineData("https://docs.google.com/document/d/abcdefghij/edit")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/view")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/edit?usp=sharing")]
    [InlineData("https://docs.google.com/document/d/abcdefghij/edit?usp=sharing#heading=h.a")]
    public void RecognizedGoogleDocsLinksProduceCanonicalMessagePreviews(string url)
    {
        var source = Assert.Single(CommunityChannelEmbeds.FindGoogleDocs($"Read this: {url}"));
        Assert.Equal("abcdefghij", source.DocumentId);
        Assert.Equal("https://docs.google.com/document/d/abcdefghij/view", source.OpenUrl);
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=document")]
    [InlineData("https://docs.google.com/spreadsheets/d/abcdefghij/edit")]
    [InlineData("http://docs.google.com/document/d/abcdefghij/edit")]
    public void NonDocumentGoogleLinksDoNotProducePreviews(string url) =>
        Assert.Empty(CommunityChannelEmbeds.FindGoogleDocs(url));

    [Fact]
    public void MessagePreviewsPreserveSourceOrderDeduplicateDocumentIdentityAndEnforceCap()
    {
        var content = """
            https://docs.google.com/document/d/first_doc_123/view
            https://docs.google.com/document/d/first_doc_123/edit?usp=sharing
            https://docs.google.com/document/d/second_doc_456/edit
            https://docs.google.com/document/d/third_doc_789/view
            https://docs.google.com/document/d/fourth_doc_012/edit
            """;

        var found = CommunityChannelEmbeds.FindGoogleDocs(content);
        Assert.Equal(CommunityChannelEmbeds.MaximumMessageDocumentPreviews, found.Count);
        Assert.Equal(["first_doc_123", "second_doc_456", "third_doc_789"],
            found.Select(value => value.DocumentId));
    }

    [Fact]
    public void MessageRowMountsSharedRendererWithCollapsedLocalStateAndEditStableKey()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var embeds = Source("Iridium.Web", "Components", "MessageDocumentEmbeds.razor");
        var preview = Source("Iridium.Web", "Components", "MessageDocumentPreview.razor");
        var styles = Source("Iridium.Web", "Components", "MessageDocumentPreview.razor.css");

        Assert.Contains("<MessageDocumentEmbeds MessageId=\"Message.Id\"", row);
        Assert.Contains("Content=\"@Message.Content\"", row);
        Assert.True(row.IndexOf("<MentionedMessageContent Content=\"@Message.Content\"", StringComparison.Ordinal) <
                    row.IndexOf("<MessageDocumentEmbeds", StringComparison.Ordinal));
        Assert.DoesNotContain("Message.Content =", row);
        Assert.Contains("Message.DeliveryState == MessageDeliveryState.Confirmed", row);
        Assert.Contains("CommunityChannelEmbeds.FindSupportedContent(Content)", embeds);
        Assert.Contains("$\"{MessageId:N}:{document.CacheIdentity}\"", embeds);
        Assert.Contains("<EmbeddedDocumentView", preview);
        Assert.Contains("MessageId=\"MessageId\" DocumentId=\"@Source.RequestIdentity\"", preview);
        Assert.Contains("private bool _expanded", preview);
        Assert.Contains("_expanded = false", preview);
        Assert.Contains("Collapse document", preview);
        Assert.Contains("Expand document", preview);
        Assert.Contains("Loading document…", preview);
        Assert.Contains("couldn't be loaded.", preview);
        Assert.Contains("Open in @Source.ProviderName", preview);
        Assert.Contains("Refreshing", preview);
        Assert.Contains("RefreshCommunityMessageEmbedDocumentAsync", preview);
        Assert.Contains(".collapsed .message-document-body{height:20rem;max-height:20rem;overflow-y:auto", styles);
        Assert.Contains(".expanded .message-document-body{height:auto;max-height:none;overflow:visible}", styles);
        Assert.Contains("height:15rem;max-height:15rem", styles);
    }

    [Fact]
    public void MessageDocumentEndpointsBindSourceToAuthorizedStoredMessageHosts()
    {
        var endpoints = Source("Iridium.Server", "Api", "MessageDocumentEndpoints.cs");
        Assert.Contains("value.Id == messageId", endpoints);
        Assert.Contains("value.CommunityId == communityId", endpoints);
        Assert.Contains("value.ChannelId == channelId", endpoints);
        Assert.Contains("CommunityPermission.ViewChannels", endpoints);
        Assert.Contains("CommunityPermission.ReadMessageHistory", endpoints);
        Assert.Contains("value.ParticipantAAccountId == session.AccountId", endpoints);
        Assert.Contains("value.ParticipantBAccountId == session.AccountId", endpoints);
        Assert.Contains("CommunityChannelEmbeds.FindSupportedContent(content)", endpoints);
        Assert.Contains("documents.GetAsync(source", endpoints);
        Assert.Contains("documents.GetMediaAsync(source", endpoints);
        Assert.DoesNotContain("new GoogleDocsDocumentParser", endpoints);
    }

    private static readonly string Root = FindRoot();
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string FindRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the Iridium solution root.");
    }
}
