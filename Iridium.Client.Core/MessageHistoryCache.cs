using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class MessageHistoryCacheDefaults
{
    public const int RecentMessagesPerConversation = 300;
    public const int MaximumMessages = 10_000;
    public const int InactiveMaximumAgeDays = 120;
}

public enum MessageHistoryConversationKind
{
    Channel,
    Direct
}

public readonly record struct MessageHistoryCacheScope(
    string NodeKey,
    Guid AccountId,
    MessageHistoryConversationKind Kind,
    Guid ConversationId)
{
    public string AccountKey => $"{NodeKey}|account:{AccountId:N}";
    public string ConversationKey => $"{AccountKey}|{Kind.ToString().ToLowerInvariant()}:{ConversationId:N}";

    public static MessageHistoryCacheScope Channel(Uri node, Guid accountId, Guid channelId) =>
        new(NormalizeNode(node), accountId, MessageHistoryConversationKind.Channel, channelId);

    public static MessageHistoryCacheScope Direct(Uri node, Guid accountId, Guid conversationId) =>
        new(NormalizeNode(node), accountId, MessageHistoryConversationKind.Direct, conversationId);

    public static string NormalizeNode(Uri node) =>
        node.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
}

public interface IMessageHistoryCache
{
    // This deliberately stores private message content on this browser only. Cache implementations must never sync it.
    Task<MessageHistoryPage<ChannelMessageDto>?> GetRecentChannelAsync(
        MessageHistoryCacheScope scope, CancellationToken cancellationToken = default);
    Task<MessageHistoryPage<DirectMessageDto>?> GetRecentDirectAsync(
        MessageHistoryCacheScope scope, CancellationToken cancellationToken = default);
    Task ReconcileRecentChannelAsync(MessageHistoryCacheScope scope,
        MessageHistoryPage<ChannelMessageDto> page, CancellationToken cancellationToken = default);
    Task ReconcileRecentDirectAsync(MessageHistoryCacheScope scope,
        MessageHistoryPage<DirectMessageDto> page, CancellationToken cancellationToken = default);
    Task UpsertChannelAsync(MessageHistoryCacheScope scope, IReadOnlyList<ChannelMessageDto> messages,
        CancellationToken cancellationToken = default);
    Task UpsertDirectAsync(MessageHistoryCacheScope scope, IReadOnlyList<DirectMessageDto> messages,
        CancellationToken cancellationToken = default);
    Task RemoveMessageAsync(MessageHistoryCacheScope scope, Guid messageId,
        CancellationToken cancellationToken = default);
    Task ClearConversationAsync(MessageHistoryCacheScope scope, CancellationToken cancellationToken = default);
    Task ClearCommunityAsync(string nodeKey, Guid accountId, Guid communityId,
        CancellationToken cancellationToken = default);
    Task ClearAccountAsync(string nodeKey, Guid accountId, CancellationToken cancellationToken = default);
    Task ClearNodeAsync(string nodeKey, CancellationToken cancellationToken = default);
    Task PruneAsync(CancellationToken cancellationToken = default);
}

internal sealed class NullMessageHistoryCache : IMessageHistoryCache
{
    public static NullMessageHistoryCache Instance { get; } = new();
    public Task<MessageHistoryPage<ChannelMessageDto>?> GetRecentChannelAsync(MessageHistoryCacheScope scope,
        CancellationToken cancellationToken = default) => Task.FromResult<MessageHistoryPage<ChannelMessageDto>?>(null);
    public Task<MessageHistoryPage<DirectMessageDto>?> GetRecentDirectAsync(MessageHistoryCacheScope scope,
        CancellationToken cancellationToken = default) => Task.FromResult<MessageHistoryPage<DirectMessageDto>?>(null);
    public Task ReconcileRecentChannelAsync(MessageHistoryCacheScope scope, MessageHistoryPage<ChannelMessageDto> page,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReconcileRecentDirectAsync(MessageHistoryCacheScope scope, MessageHistoryPage<DirectMessageDto> page,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpsertChannelAsync(MessageHistoryCacheScope scope, IReadOnlyList<ChannelMessageDto> messages,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpsertDirectAsync(MessageHistoryCacheScope scope, IReadOnlyList<DirectMessageDto> messages,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveMessageAsync(MessageHistoryCacheScope scope, Guid messageId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearConversationAsync(MessageHistoryCacheScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearCommunityAsync(string nodeKey, Guid accountId, Guid communityId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearAccountAsync(string nodeKey, Guid accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearNodeAsync(string nodeKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class MessageHistoryReconciliation
{
    public static IReadOnlyList<ChannelMessageDto> Channel(
        IEnumerable<ChannelMessageDto> existing, IEnumerable<ChannelMessageDto> authoritative, int limit = 300) =>
        Merge(existing, authoritative, message => message.Id, message => message.ClientMessageId,
            message => message.CreatedAt, message => message.IsDeleted,
            message => message.DeliveryState, limit);

    public static IReadOnlyList<DirectMessageDto> Direct(
        IEnumerable<DirectMessageDto> existing, IEnumerable<DirectMessageDto> authoritative, int limit = 300) =>
        Merge(existing, authoritative, message => message.Id, message => message.ClientMessageId,
            message => message.CreatedAt, message => message.IsDeleted,
            message => message.DeliveryState, limit);

    private static IReadOnlyList<T> Merge<T>(IEnumerable<T> existing, IEnumerable<T> authoritative,
        Func<T, Guid> id, Func<T, Guid?> clientId, Func<T, DateTimeOffset> createdAt,
        Func<T, bool> deleted, Func<T, MessageDeliveryState> delivery, int limit)
    {
        var result = existing.Where(value => !deleted(value)).ToDictionary(id);
        foreach (var value in authoritative)
        {
            if (deleted(value)) { result.Remove(id(value)); continue; }
            if (clientId(value) is { } canonicalClientId)
                foreach (var duplicate in result.Where(pair => clientId(pair.Value) == canonicalClientId).Select(pair => pair.Key).ToArray())
                    result.Remove(duplicate);
            result[id(value)] = value;
        }
        var ordered = result.Values.OrderBy(createdAt).ThenBy(id).ToArray();
        if (ordered.Length <= limit) return ordered;
        var local = ordered.Where(value => delivery(value) != MessageDeliveryState.Confirmed).ToArray();
        return ordered.Where(value => delivery(value) == MessageDeliveryState.Confirmed).TakeLast(Math.Max(0, limit - local.Length))
            .Concat(local).OrderBy(createdAt).ThenBy(id).ToArray();
    }
}
