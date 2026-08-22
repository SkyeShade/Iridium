using System.Collections.Concurrent;

namespace Iridium.Server.Hubs;

// TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
public sealed class VoiceConnectionRegistry(IHostEnvironment environment, ILogger<VoiceConnectionRegistry> logger)
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _connections = [];

    public void Connected(Guid accountId, string connectionId)
    {
        var accountConnections = _connections.GetOrAdd(accountId, static _ => new());
        accountConnections.TryAdd(connectionId, 0);
        Diagnostic("REGISTER", accountId, connectionId, accountConnections.Count);
    }

    public void Disconnected(Guid accountId, string connectionId)
    {
        if (!_connections.TryGetValue(accountId, out var accountConnections)) return;
        accountConnections.TryRemove(connectionId, out _);
        Diagnostic("UNREGISTER", accountId, connectionId, accountConnections.Count);
        if (accountConnections.IsEmpty) _connections.TryRemove(
            new KeyValuePair<Guid, ConcurrentDictionary<string, byte>>(accountId, accountConnections));
    }

    public IReadOnlyList<string> ForAccount(Guid accountId) =>
        _connections.TryGetValue(accountId, out var accountConnections)
            ? accountConnections.Keys.Order(StringComparer.Ordinal).ToArray()
            : [];

    private void Diagnostic(string operation, Guid accountId, string connectionId, int count)
    {
        if (!environment.IsDevelopment()) return;
        logger.LogDebug("VOICE CONNECTION {Operation}: AccountId={AccountId} ConnectionId={ConnectionId} Count={Count}",
            operation, accountId, connectionId.Length <= 8 ? connectionId : connectionId[..8], count);
    }
}
