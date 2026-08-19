using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageGroupingTests
{
    [Fact]
    public void SameAuthorAtExactlyOneMinuteRemainsGrouped()
    {
        var author = Guid.NewGuid();
        var first = Message(author, DateTimeOffset.Parse("2026-08-19T12:00:00Z"));
        var second = Message(author, first.CreatedAt.AddMinutes(1));

        Assert.False(MessageGrouping.StartsNewGroup(first, second));
    }

    [Fact]
    public void SameAuthorAfterMoreThanOneMinuteStartsNewGroup()
    {
        var author = Guid.NewGuid();
        var first = Message(author, DateTimeOffset.Parse("2026-08-19T12:00:00Z"));
        var second = Message(author, first.CreatedAt.AddMinutes(1).AddTicks(1));

        Assert.True(MessageGrouping.StartsNewGroup(first, second));
    }

    [Fact]
    public void AuthorChangeAndReplyInterruptGrouping()
    {
        var first = Message(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var otherAuthor = Message(Guid.NewGuid(), first.CreatedAt.AddSeconds(2));
        var reply = Message(first.Author.AccountId, first.CreatedAt.AddSeconds(3)) with
        {
            ReplyTo = new MessageReplyDto(first.Id, first.Author.AccountId, first.Author.DisplayName, first.Content, false)
        };

        Assert.True(MessageGrouping.StartsNewGroup(first, otherAuthor));
        Assert.True(MessageGrouping.StartsNewGroup(first, reply));
    }

    private static ChannelMessageDto Message(Guid authorId, DateTimeOffset at) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        new MessageAuthorDto(authorId, "user", "User"),
        "hello",
        at,
        null,
        false,
        null);
}
