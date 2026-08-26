using Iridium.Protocol;
using Iridium.UI;

namespace Iridium.Tests;

public sealed class OptimisticMessageGroupingTests
{
    [Fact]
    public void MatchingConfirmedPresentationGroupsPendingCommunityMessageImmediately()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Aria", 7, pending: true);

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void CanonicalReplacementReturnsToCanonicalGroupingAndRemainsCompact()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Aria", 7, pending: true);
        var canonical = pending with
        {
            Id = Guid.NewGuid(),
            DeliveryState = MessageDeliveryState.Confirmed,
            Author = pending.Author with
            {
                AvatarSnapshotMessageId = Guid.NewGuid(),
                HasHistoricalSnapshot = true
            }
        };

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, canonical));
    }

    [Theory]
    [InlineData("GM Skye", 7)]
    [InlineData("Aria", 8)]
    public void ChangedCommunityAvatarPresentationStartsPendingGroup(string displayName, long avatarRevision)
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), displayName, avatarRevision, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void DifferentAuthorStartsPendingGroup()
    {
        var confirmed = Message(Guid.NewGuid(), At, "Aria", 7, historical: true);
        var pending = Message(Guid.NewGuid(), At.AddSeconds(5), "Aria", 7, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void PendingMessageOutsideGroupingWindowStartsGroup()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddMinutes(1).AddTicks(1), "Aria", 7, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void PendingReplyStartsGroup()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Aria", 7, pending: true) with
        {
            ReplyTo = new(confirmed.Id, authorId, "Aria", "Earlier", false)
        };

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void FirstPendingMessageStartsGroup()
    {
        var pending = Message(Guid.NewGuid(), At, "Aria", 7, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(null, pending));
    }

    [Fact]
    public void MatchingLegacyDefaultPresentationGroupsPendingMessage()
    {
        var authorId = Guid.NewGuid();
        var legacy = Message(authorId, At, "Skye", 12);
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 12, pending: true);

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(legacy, pending));
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

    private static ChannelMessageDto Message(Guid authorId, DateTimeOffset at, string displayName,
        long avatarRevision, bool historical = false, bool pending = false) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        new(authorId, "user", displayName, AvatarRevision: avatarRevision,
            AvatarSnapshotMessageId: historical ? Guid.NewGuid() : null,
            HasHistoricalSnapshot: historical),
        "hello",
        at,
        null,
        false,
        null,
        DeliveryState: pending ? MessageDeliveryState.Pending : MessageDeliveryState.Confirmed);
}
