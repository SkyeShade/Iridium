using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class ExternalEmbedTests
{
    private readonly ExternalEmbedResolver _resolver = new([new YouTubeEmbedProvider()]);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    public void SupportedYouTubeUrlsResolveToTrustedLazyEmbedData(string url)
    {
        var embed = Assert.Single(_resolver.Resolve(url));
        Assert.Equal("dQw4w9WgXcQ", embed.VideoId);
        Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", embed.EmbedUrl);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", embed.ThumbnailUrl);
        Assert.Equal(url, embed.OriginalUrl);
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=90", 90)]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=1m30s", 90)]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&start=125", 125)]
    public void TimestampIsWhitelistedAndNormalized(string url, int expected)
    {
        var embed = Assert.Single(_resolver.Resolve(url));
        Assert.Equal(expected, embed.StartSeconds);
        Assert.EndsWith($"?start={expected}", embed.EmbedUrl);
        Assert.DoesNotContain("&t=", embed.EmbedUrl);
    }

    [Theory]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=too-short")]
    [InlineData("youtube dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ/extra")]
    public void LookalikeAndMalformedUrlsDoNotResolve(string content) => Assert.Empty(_resolver.Resolve(content));

    [Fact]
    public void ResolverUsesParsedMarkdownLinksAndCapsHeavyEmbedsAtThree()
    {
        const string content = "[one](https://youtu.be/dQw4w9WgXcQ)\n" +
                               "https://youtu.be/aqz-KE-bpKQ\nhttps://youtu.be/9bZkp7q19f0\n" +
                               "https://youtu.be/3JZ_D3ELwOQ";
        var embeds = _resolver.Resolve(content);
        Assert.Equal(ExternalEmbedResolver.MaximumEmbedsPerMessage, embeds.Count);
        Assert.Equal("dQw4w9WgXcQ", embeds[0].VideoId);
    }

    [Fact]
    public void DuplicateVideoAndTimestampCombinationProducesOneEmbed()
    {
        var embeds = _resolver.Resolve("https://youtu.be/dQw4w9WgXcQ?t=90 https://youtu.be/dQw4w9WgXcQ?t=90");
        Assert.Single(embeds);
    }

    [Fact]
    public void HiddenSpoilerLinkDoesNotLeakAThumbnailEmbed() =>
        Assert.Empty(_resolver.Resolve("||https://youtu.be/dQw4w9WgXcQ||"));
}
