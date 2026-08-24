using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageTimelineTests
{
    [Fact]
    public void DeletedMessagesAreOmittedAndRemainingMessagesRegroupNaturally()
    {
        var authorId = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-08-24T12:00:00Z");
        var first = Message(authorId, at, "first");
        var deleted = Message(authorId, at.AddSeconds(30), string.Empty) with { IsDeleted = true };
        var last = Message(authorId, at.AddMinutes(1), "last");

        var visible = MessageTimeline.Visible([first, deleted, last]);

        Assert.Equal([first.Id, last.Id], visible.Select(message => message.Id));
        Assert.False(MessageGrouping.StartsNewGroup(visible[0], visible[1]));
    }

    [Fact]
    public void RealtimeDeletionRemovesRowAndPreservesDeletedReplyTombstoneWithoutContent()
    {
        var authorId = Guid.NewGuid();
        var first = Message(authorId, DateTimeOffset.UtcNow, "secret deleted content");
        var reply = Message(Guid.NewGuid(), first.CreatedAt.AddSeconds(1), "reply") with
        {
            ReplyTo = new(first.Id, authorId, "Skye", first.Content, false)
        };
        var messages = new List<ChannelMessageDto> { first, reply };

        MessageTimeline.ApplyDeletion(messages, first.Id);

        var remaining = Assert.Single(messages);
        Assert.Equal(reply.Id, remaining.Id);
        Assert.True(remaining.ReplyTo!.IsDeleted);
        Assert.Null(remaining.ReplyTo.Excerpt);
        Assert.DoesNotContain(messages, message => message.Content.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void PaginationProjectionCannotReinsertDeletedRows()
    {
        var deleted = Message(Guid.NewGuid(), DateTimeOffset.UtcNow, string.Empty) with { IsDeleted = true };
        Assert.Empty(MessageTimeline.Visible([deleted]));
    }

    private static ChannelMessageDto Message(Guid authorId, DateTimeOffset at, string content) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(authorId, "user", "User"), content, at, null, false, null);
}
