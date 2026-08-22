using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class EmojiMessageParserTests
{
    [Fact]
    public void Unicode17CatalogCoversMajorGroupsAndCommonEmoji()
    {
        Assert.True(StandardEmojiCatalog.All.Count > 3500);
        string[] glyphs = ["😀", "👋", "❤️", "🐱", "🍕", "⚽", "🚗", "💡", "✅", "🇩🇰"];
        Assert.All(glyphs, glyph => Assert.Contains(StandardEmojiCatalog.All, value => value.Glyph == glyph));
        string[] groups = ["Smileys & Emotion", "People & Body", "Animals & Nature", "Food & Drink",
            "Activities", "Travel & Places", "Objects", "Symbols", "Flags"];
        Assert.All(groups, group => Assert.Contains(StandardEmojiCatalog.All, value => value.Category == group));
    }
    [Fact]
    public void NamesNormalizeAndValidate()
    {
        Assert.Equal("blob_wave", CommunityEmojiNames.Normalize("Blob-Wave.gif"));
        Assert.True(CommunityEmojiNames.IsValid("blob_wave"));
        Assert.False(CommunityEmojiNames.IsValid("bad name"));
    }

    [Fact] public void MixedMessageStaysInline() => Assert.False(EmojiMessageParser.IsLargeEmojiOnly("hello \U0001F44B"));
    [Fact] public void SmallUnicodeEmojiOnlyMessageIsLarge() => Assert.True(EmojiMessageParser.IsLargeEmojiOnly("\U0001F44B \u2764\uFE0F"));

    [Fact]
    public void CustomEmojiOnlyMessageIsLargeAndStableIdParses()
    {
        var id = Guid.NewGuid();
        var token = CommunityEmojiNames.Token(id, "blob_wave");
        Assert.True(EmojiMessageParser.IsLargeEmojiOnly(token));
        Assert.Equal(id, EmojiMessageParser.Parse(token).Single().EmojiId);
    }

    [Fact]
    public void MoreThanFiveEmojiDoesNotBecomeLarge() =>
        Assert.False(EmojiMessageParser.IsLargeEmojiOnly("\U0001F600 \U0001F600 \U0001F600 \U0001F600 \U0001F600 \U0001F600"));

    [Theory]
    [InlineData("\U0001F44D\U0001F3FD", "1f44d-1f3fd")]
    [InlineData("\U0001F469\u200D\U0001F4BB", "1f469-200d-1f4bb")]
    [InlineData("\U0001F1E9\U0001F1F0", "1f1e9-1f1f0")]
    public void StandardArtworkParsingKeepsCompleteEmojiSequences(string glyph, string artworkKey)
    {
        var part = Assert.Single(EmojiMessageParser.Parse(glyph));
        Assert.Equal(artworkKey, part.StandardArtworkKey);
        Assert.Equal(glyph, part.StandardGlyph);
        Assert.True(EmojiMessageParser.IsLargeEmojiOnly(glyph));
    }
}
