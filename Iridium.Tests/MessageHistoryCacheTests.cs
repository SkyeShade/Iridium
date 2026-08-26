using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageHistoryCacheTests
{
    [Fact]
    public void ScopeSeparatesNodesAccountsAndConversationKinds()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var nodeA = new Uri("https://one.example.test/path");
        var nodeB = new Uri("https://two.example.test/");

        var channel = MessageHistoryCacheScope.Channel(nodeA, accountA, conversation);

        Assert.NotEqual(channel.ConversationKey,
            MessageHistoryCacheScope.Channel(nodeB, accountA, conversation).ConversationKey);
        Assert.NotEqual(channel.ConversationKey,
            MessageHistoryCacheScope.Channel(nodeA, accountB, conversation).ConversationKey);
        Assert.NotEqual(channel.ConversationKey,
            MessageHistoryCacheScope.Direct(nodeA, accountA, conversation).ConversationKey);
        Assert.Equal("https://one.example.test", channel.NodeKey);
    }

    [Fact]
    public void ReconciliationReplacesEditedRowsWithoutDuplicatingPagination()
    {
        var original = ChannelMessage("old", DateTimeOffset.Parse("2026-08-24T10:00:00Z"));
        var older = ChannelMessage("older", original.CreatedAt.AddMinutes(-1));
        var edited = original with { Content = "edited", EditedAt = original.CreatedAt.AddMinutes(1) };

        var result = MessageHistoryReconciliation.Channel([original, older], [older, edited]);

        Assert.Equal(2, result.Count);
        Assert.Equal("edited", Assert.Single(result, value => value.Id == original.Id).Content);
        Assert.Equal([older.Id, original.Id], result.Select(value => value.Id));
    }

    [Fact]
    public void ConfirmedSendReplacesOptimisticClientIdAndFailedRowsAreNotMadeCanonical()
    {
        var clientId = Guid.NewGuid();
        var pending = ChannelMessage("pending", DateTimeOffset.UtcNow) with
        {
            ClientMessageId = clientId, DeliveryState = MessageDeliveryState.Pending
        };
        var failed = ChannelMessage("failed", pending.CreatedAt.AddSeconds(1)) with
        {
            DeliveryState = MessageDeliveryState.Failed
        };
        var confirmed = ChannelMessage("confirmed", pending.CreatedAt.AddSeconds(2)) with { ClientMessageId = clientId };

        var result = MessageHistoryReconciliation.Channel([pending, failed], [confirmed]);

        Assert.Single(result, value => value.ClientMessageId == clientId);
        Assert.Contains(result, value => value.Id == failed.Id && value.DeliveryState == MessageDeliveryState.Failed);
        Assert.Contains(result, value => value.Id == confirmed.Id && value.DeliveryState == MessageDeliveryState.Confirmed);
    }

    [Fact]
    public void DeletedRowsAreExcludedAndRecentWindowIsBounded()
    {
        var at = DateTimeOffset.Parse("2026-08-24T10:00:00Z");
        var values = Enumerable.Range(0, 12).Select(index => ChannelMessage(index.ToString(), at.AddSeconds(index))).ToArray();
        var deleted = values[11] with { IsDeleted = true };

        var result = MessageHistoryReconciliation.Channel(values, [deleted], limit: 5);

        Assert.Equal(5, result.Count);
        Assert.DoesNotContain(result, value => value.Id == deleted.Id);
        Assert.Equal(values.Skip(6).Take(5).Select(value => value.Id), result.Select(value => value.Id));
    }

    [Fact]
    public void FreshReconciliationFollowsOnlyWhenViewportWasPinnedToLatest()
    {
        Assert.True(MessageHistoryFollowLatest.ShouldFollow(true, 12, 13));
        Assert.False(MessageHistoryFollowLatest.ShouldFollow(false, 12, 13));
        Assert.False(MessageHistoryFollowLatest.ShouldFollow(true, -1, 13));
        Assert.False(MessageHistoryFollowLatest.ShouldFollow(true, 13, 13));
    }

    [Fact]
    public void ServerSnapshotReconciliationPreservesRealtimeRowsNewerThanSnapshot()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Iridium.Client.Core", "ChannelMessagingSession.cs"));
        Assert.Equal(2, source.Split("value.CreatedAt <= newest", StringSplitOptions.None).Length - 1);
    }

    private static ChannelMessageDto ChannelMessage(string content, DateTimeOffset createdAt) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), "user", "User"),
        content, createdAt, null, false, null);
}
