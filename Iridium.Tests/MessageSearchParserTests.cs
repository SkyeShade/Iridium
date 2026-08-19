using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageSearchParserTests
{
    [Fact]
    public void ResolvesSelectedUserAndChannelTokensToStableIds()
    {
        var accountId = Guid.NewGuid();
        var communityId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var member = new CommunityMemberDto(accountId, "skye", "Skye", null, null, null,
            DateTimeOffset.UtcNow, false, PublicPresence.Online, []);
        var channel = new CommunityChannelDto(channelId, communityId, null, "general", 0, DateTimeOffset.UtcNow);
        var selected = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["from:skye"] = accountId,
            ["mentions:skye"] = accountId,
            ["in:general"] = channelId
        };

        var result = MessageSearchParser.Parse("from:skye mentions:skye in:general reactor",
            new([member], [channel], selected, TimeZoneInfo.Utc));

        Assert.True(result.IsValid);
        Assert.Equal(accountId, result.Query.FromAccountId);
        Assert.Equal(accountId, result.Query.MentionedAccountId);
        Assert.Equal(channelId, result.Query.ChannelId);
        Assert.Equal("reactor", result.Query.Text);
    }

    [Fact]
    public void DuringDateProducesOneUtcCalendarDay()
    {
        var result = MessageSearchParser.Parse("during:2026-08-12 screenshot",
            new([], [], null, TimeZoneInfo.Utc));

        Assert.True(result.IsValid);
        Assert.Equal(DateTimeOffset.Parse("2026-08-12T00:00:00Z"), result.Query.DuringStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-13T00:00:00Z"), result.Query.DuringEndUtc);
        Assert.Equal("screenshot", result.Query.Text);
    }

    [Fact]
    public void InvalidDateIsReportedWithoutThrowing()
    {
        var result = MessageSearchParser.Parse("after:not-a-date reactor", new([], [], null, TimeZoneInfo.Utc));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("Invalid after date", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesSupportedContentAndAuthorFilters()
    {
        var result = MessageSearchParser.Parse("has:link author:user", new([], [], null, TimeZoneInfo.Utc),
            MessageSearchSort.Oldest);

        Assert.True(result.IsValid);
        Assert.Contains(MessageSearchContentType.Link, result.Query.HasTypes);
        Assert.Equal(MessageAuthorType.User, result.Query.AuthorType);
        Assert.Equal(MessageSearchSort.Oldest, result.Query.Sort);
    }
}
