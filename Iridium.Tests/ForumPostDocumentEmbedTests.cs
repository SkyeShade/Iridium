using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class ForumPostDocumentEmbedTests
{
    [Fact]
    public void PermissionAndForumDefaultsAreSafe()
    {
        Assert.True((CommunityPermission.All & CommunityPermission.EmbedDocumentsInForumPosts) != 0);
        var channel = new CommunityChannelDto(Guid.NewGuid(), Guid.NewGuid(), null, "forum", 0,
            DateTimeOffset.UtcNow, Kind: CommunityChannelKind.Forum);
        Assert.False(channel.AllowDocumentEmbeds);
        var post = new CommunityForumPostDto(Guid.NewGuid(), channel.CommunityId, channel.Id, Guid.NewGuid(),
            Guid.NewGuid(), new(Guid.NewGuid(), "author", "Author"), "Post", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, false, false);
        Assert.Null(post.EmbedProvider);
        Assert.Null(post.EmbedUrl);
    }

    [Fact]
    public void ForumUiUsesTheSharedNativeDocumentRendererAndPermissionGate()
    {
        var view = Source("Iridium.Web", "Components", "ForumChannelView.razor");
        var channelView = Source("Iridium.Web", "Components", "ChannelView.razor");
        var block = Source("Iridium.Web", "Components", "EmbeddedDocumentBlock.razor");
        var card = Source("Iridium.Web", "Components", "ForumPostCard.razor");

        Assert.Contains("Channel.AllowDocumentEmbeds", view);
        Assert.Contains("CommunityPermission.EmbedDocumentsInForumPosts", view);
        Assert.Contains("ForumPostEmbedProvider=\"selectedPost.EmbedProvider\"", view);
        Assert.Contains("ForumPostEmbedUrl=\"@selectedPost.EmbedUrl\"", view);
        Assert.DoesNotContain("ForumPostEmbedUrl=\"selectedPost.EmbedUrl\"", view);
        Assert.Contains("Session.GetForumPostAsync", view);
        Assert.Contains("_postLoadCancellation?.Cancel()", view);
        Assert.Contains("Session.GetForumPostEmbedDocumentAsync", channelView);
        Assert.Contains("_loadedEmbedForumPostId != ForumPostId", channelView);
        Assert.Contains("_loadedEmbedProvider != ActiveEmbedProvider", channelView);
        Assert.Contains("OperationCanceledException", channelView);
        Assert.Contains("EmbeddedDocumentView", channelView);
        Assert.Contains("DownloadForumPostEmbedDocumentMediaAsync", block);
        Assert.Contains("Post.EmbedProvider is { } provider", card);
    }

    [Fact]
    public void ForumEndpointsReuseTheGoogleDocsProviderService()
    {
        var endpoint = Source("Iridium.Server", "Api", "CommunityForumEndpoints.cs");
        Assert.Contains("IEmbeddedContentService documents", endpoint);
        Assert.Contains("documents.GetAsync(configuration", endpoint);
        Assert.Contains("documents.GetMediaAsync(configuration", endpoint);
        Assert.DoesNotContain("new GoogleDocsDocumentParser", endpoint);
        Assert.Contains("includeEmbedUrl: false", endpoint);
        var documentEndpoint = Slice(endpoint, "private static async Task<IResult> GetEmbedDocumentAsync",
            "private static async Task<IResult> GetEmbedDocumentMediaAsync");
        var mediaEndpoint = Slice(endpoint, "private static async Task<IResult> GetEmbedDocumentMediaAsync",
            "private static async Task<IResult> ListAsync");
        foreach (var handler in new[] { documentEndpoint, mediaEndpoint })
        {
            Assert.Contains("value.Id == postId", handler);
            Assert.Contains("value.CommunityId == communityId", handler);
            Assert.Contains("value.ForumChannelId == channelId", handler);
            Assert.Contains("CommunityPermission.ViewChannels", handler);
            Assert.DoesNotContain("EmbedDocumentsInForumPosts", handler);
        }
    }

    [Fact]
    public void OpenForumPostPlacesTheSharedDocumentBeforeDiscussionMessages()
    {
        var channelView = Source("Iridium.Web", "Components", "ChannelView.razor");
        var messageList = Source("Iridium.Web", "Components", "MessageList.razor");

        var leadingContentStart = channelView.IndexOf("<BeforeMessages>", StringComparison.Ordinal);
        var document = channelView.IndexOf("<section class=\"channel-document\"", StringComparison.Ordinal);
        var separator = channelView.IndexOf("class=\"channel-document-separator\"", StringComparison.Ordinal);
        var leadingContentEnd = channelView.IndexOf("</BeforeMessages>", StringComparison.Ordinal);
        Assert.True(leadingContentStart >= 0 && leadingContentStart < document);
        Assert.True(document < separator && separator < leadingContentEnd);
        Assert.Contains("ForumPostId.HasValue ? \"Discussion\" : \"Messages\"", channelView);

        var renderedLeadingContent = messageList.IndexOf("@BeforeMessages", StringComparison.Ordinal);
        var messageHistory = messageList.IndexOf("data-message-history-start", StringComparison.Ordinal);
        Assert.True(renderedLeadingContent >= 0 && renderedLeadingContent < messageHistory);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")), Path.Combine(parts)));
}
