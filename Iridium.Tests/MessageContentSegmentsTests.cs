using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageContentSegmentsTests
{
    [Fact]
    public void PlainAndMultilineContentRemainUnchanged()
    {
        const string content = "line one\nline two";
        var segment = Assert.Single(MessageContentSegments.Parse(content, null));
        Assert.Equal(content, segment.Text);
        Assert.Null(segment.Mention);
    }

    [Fact]
    public void UserMentionPreservesSurroundingContent()
    {
        var accountId = Guid.NewGuid();
        const string content = "hello @Skye, welcome";
        var segments = MessageContentSegments.Parse(content,
            [new(CommunityMentionKind.Account, accountId, 6, 5, "@Skye")]);

        Assert.Equal("hello @Skye, welcome", Render(segments));
        Assert.Equal(accountId, Assert.Single(segments, value => value.Mention is not null).Mention?.TargetId);
    }

    [Fact]
    public void RoleAndEveryoneMentionsRenderOnceInTheirOriginalPositions()
    {
        var roleId = Guid.NewGuid();
        const string content = "@Admin please tell @everyone";
        var segments = MessageContentSegments.Parse(content,
        [
            new(CommunityMentionKind.Role, roleId, 0, 6, "@Admin"),
            new(CommunityMentionKind.Everyone, null, 19, 9, "@everyone")
        ]);

        Assert.Equal(content, Render(segments));
        Assert.Equal(2, segments.Count(value => value.Mention is not null));
    }

    private static string Render(IEnumerable<MessageContentSegment> segments) => string.Concat(
        segments.Select(value => value.Mention?.DisplayText ?? value.Text));
}
