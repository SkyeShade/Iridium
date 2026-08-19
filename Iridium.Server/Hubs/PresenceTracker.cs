using System.Collections.Concurrent;
using Iridium.Protocol;

namespace Iridium.Server.Hubs;

public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, PresenceEntry> _entries = new();

    public PublicPresence Connected(Guid accountId, UserPresence preferred)
    {
        var entry = _entries.AddOrUpdate(accountId,
            _ => new PresenceEntry(1, preferred),
            (_, current) => current with { Connections = current.Connections + 1, Preferred = preferred });
        return Public(entry);
    }

    public PublicPresence Disconnected(Guid accountId)
    {
        if (!_entries.TryGetValue(accountId, out var current)) return PublicPresence.Offline;
        var updated = current with { Connections = Math.Max(0, current.Connections - 1) };
        if (updated.Connections == 0) _entries.TryRemove(accountId, out _);
        else _entries[accountId] = updated;
        return Public(updated);
    }

    public PublicPresence SetPreferred(Guid accountId, UserPresence preferred)
    {
        var entry = _entries.AddOrUpdate(accountId,
            _ => new PresenceEntry(0, preferred),
            (_, current) => current with { Preferred = preferred });
        return Public(entry);
    }

    public PublicPresence GetPublic(Guid accountId) =>
        _entries.TryGetValue(accountId, out var entry) ? Public(entry) : PublicPresence.Offline;

    private static PublicPresence Public(PresenceEntry entry) =>
        PresenceVisibility.ToPublic(entry.Preferred, entry.Connections > 0);

    private sealed record PresenceEntry(int Connections, UserPresence Preferred);
}
