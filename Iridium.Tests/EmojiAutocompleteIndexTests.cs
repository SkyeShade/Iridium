using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class EmojiAutocompleteIndexTests
{
    [Theory]
    [InlineData(":skull:", "skull", 0, 7)]
    [InlineData("hello :wave:", "wave", 6, 12)]
    public void ClosingColonFindsOnlyTheAliasImmediatelyBeforeCaret(string source, string alias, int start, int end)
    {
        Assert.True(EmojiAutocompleteIndex.TryAliasBeforeCaret(source, source.Length, out var range));
        Assert.Equal(new EmojiAliasRange(start, end, alias), range);
        Assert.NotNull(new EmojiAutocompleteIndex().Exact(alias, false)?.Standard);
    }

    [Theory]
    [InlineData(":")]
    [InlineData(":sku")]
    [InlineData(":notarealemoji:")]
    [InlineData("abc:skull:")]
    [InlineData("https://example.test:443")]
    public void InvalidIncompleteAndEmbeddedAliasesRemainText(string source)
    {
        var index = new EmojiAutocompleteIndex();
        var recognized = EmojiAutocompleteIndex.TryAliasBeforeCaret(source, source.Length, out var range) &&
                         index.Exact(range.Alias, false) is not null;
        Assert.False(recognized);
    }

    [Fact]
    public void StandardIndexIsSharedAndSearchIsRankedAndBounded()
    {
        var first = new EmojiAutocompleteIndex();
        var second = new EmojiAutocompleteIndex();
        Assert.Equal(1, EmojiAutocompleteIndex.StandardBuildCount);
        Assert.Equal("skull", Assert.IsType<StandardEmoji>(first.Exact("skull", false)!.Standard).Name);
        Assert.Equal("wave", Assert.IsType<StandardEmoji>(second.Exact("wave", false)!.Standard).Name);
        var results = first.Search("face", 8, false);
        Assert.InRange(results.Count, 1, 8);
        Assert.Equal(results.Count, results.Select(value => value.Alias).Distinct().Count());
    }

    [Fact]
    public void CustomIndexBuildsOnlyWhenMetadataChangesAndCollisionOrderIsDeterministic()
    {
        var index = new EmojiAutocompleteIndex();
        var alpha = Community("Alpha");
        var beta = Community("Beta");
        var first = Available(beta, "mudrock", 1);
        var second = Available(alpha, "mudrock", 1);
        index.UpdateCustom([first, second]);
        index.UpdateCustom([first, second]);
        Assert.Equal(1, index.CustomBuildCount);
        Assert.Equal(alpha.Id, index.ExactCustom("mudrock")!.Community.Id);
        Assert.Null(index.ExactCustom("unavailable"));
        index.UpdateCustom([first]);
        Assert.Equal(2, index.CustomBuildCount);
        Assert.Equal(beta.Id, index.ExactCustom("mudrock")!.Community.Id);
    }

    private static CommunityDto Community(string name) =>
        new(Guid.NewGuid(), name, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
    private static AvailableCommunityEmoji Available(CommunityDto community, string name, long revision) => new(community,
        new(Guid.NewGuid(), community.Id, name, "image/png", false, 32, 32, 100, revision,
            DateTimeOffset.UtcNow, Guid.NewGuid()));
}
