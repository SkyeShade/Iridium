using Iridium.Protocol;

namespace Iridium.Server.Voice;

public sealed record VoiceStreamPublishResult(PublishedVoiceStreamDto Stream, VoiceStreamEndedEvent? Replaced);

/// <summary>Transient authoritative publication/watch state. Media bytes never enter this service.</summary>
public sealed class VoiceStreamRegistry(TimeProvider timeProvider)
{
    private sealed record Entry(PublishedVoiceStreamDto Stream, string OwnerConnectionId);
    private readonly object _gate = new();
    private readonly Dictionary<(VoiceMediaSessionKind Kind, Guid SessionId, Guid StreamId), Entry> _streams = [];
    private readonly Dictionary<string, Guid> _watching = [];

    public IReadOnlyList<PublishedVoiceStreamDto> Get(VoiceMediaSessionKind kind, Guid sessionId)
    {
        lock (_gate) return _streams.Values.Where(value => value.Stream.SessionKind == kind &&
            value.Stream.SessionId == sessionId).Select(value => value.Stream).OrderBy(value => value.StartedAt).ToArray();
    }

    public VoiceStreamPublishResult Publish(VoiceMediaSessionKind kind, Guid sessionId, Guid accountId,
        string displayName, string connectionId, PublishVoiceStreamRequest request)
    {
        if (request.StreamId == Guid.Empty || string.IsNullOrWhiteSpace(request.MediaStreamId) ||
            request.MediaStreamId.Length > 256 || !Enum.IsDefined(request.Kind))
            throw new InvalidOperationException("The published media stream metadata is invalid.");
        lock (_gate)
        {
            var previous = _streams.Values.FirstOrDefault(value => value.Stream.SessionKind == kind &&
                value.Stream.SessionId == sessionId && value.Stream.OwnerAccountId == accountId &&
                value.Stream.Kind == request.Kind);
            VoiceStreamEndedEvent? replaced = null;
            if (previous is not null)
            {
                _streams.Remove((kind, sessionId, previous.Stream.StreamId));
                replaced = new(kind, sessionId, previous.Stream.StreamId, "Replaced");
            }
            var stream = new PublishedVoiceStreamDto(request.StreamId, kind, sessionId, accountId, displayName,
                kind == VoiceMediaSessionKind.CommunityVoice ? connectionId : null, request.Kind,
                request.HasAudio, request.MediaStreamId.Trim(), timeProvider.GetUtcNow());
            _streams[(kind, sessionId, request.StreamId)] = new(stream, connectionId);
            return new(stream, replaced);
        }
    }

    public PublishedVoiceStreamDto? Update(VoiceMediaSessionKind kind, Guid sessionId, Guid streamId,
        string ownerConnectionId, bool hasAudio)
    {
        lock (_gate)
        {
            var key = (kind, sessionId, streamId);
            if (!_streams.TryGetValue(key, out var entry) || entry.OwnerConnectionId != ownerConnectionId)
                return null;
            var stream = entry.Stream with { HasAudio = hasAudio };
            _streams[key] = entry with { Stream = stream };
            return stream;
        }
    }

    public VoiceStreamEndedEvent? Stop(VoiceMediaSessionKind kind, Guid sessionId, Guid streamId,
        string ownerConnectionId, string reason)
    {
        lock (_gate)
        {
            var key = (kind, sessionId, streamId);
            if (!_streams.TryGetValue(key, out var entry) || entry.OwnerConnectionId != ownerConnectionId) return null;
            _streams.Remove(key);
            RemoveWatchersLocked(streamId);
            return new(kind, sessionId, streamId, reason);
        }
    }

    public bool Watch(string connectionId, VoiceMediaSessionKind kind, Guid sessionId, Guid streamId)
    {
        lock (_gate)
        {
            if (!_streams.ContainsKey((kind, sessionId, streamId))) return false;
            _watching[connectionId] = streamId;
            return true;
        }
    }

    public void StopWatching(string connectionId, Guid streamId)
    {
        lock (_gate)
            if (_watching.GetValueOrDefault(connectionId) == streamId) _watching.Remove(connectionId);
    }

    public IReadOnlyList<VoiceStreamEndedEvent> RemoveConnection(string connectionId, string reason)
    {
        lock (_gate)
        {
            _watching.Remove(connectionId);
            var owned = _streams.Where(value => value.Value.OwnerConnectionId == connectionId).ToArray();
            var ended = new List<VoiceStreamEndedEvent>(owned.Length);
            foreach (var value in owned)
            {
                _streams.Remove(value.Key);
                RemoveWatchersLocked(value.Value.Stream.StreamId);
                ended.Add(new(value.Key.Kind, value.Key.SessionId, value.Key.StreamId, reason));
            }
            return ended;
        }
    }

    public IReadOnlyList<VoiceStreamEndedEvent> RemoveSession(VoiceMediaSessionKind kind, Guid sessionId, string reason)
    {
        lock (_gate)
        {
            var values = _streams.Where(value => value.Key.Kind == kind && value.Key.SessionId == sessionId).ToArray();
            foreach (var value in values)
            {
                _streams.Remove(value.Key);
                RemoveWatchersLocked(value.Key.StreamId);
            }
            return values.Select(value => new VoiceStreamEndedEvent(kind, sessionId, value.Key.StreamId, reason)).ToArray();
        }
    }

    private void RemoveWatchersLocked(Guid streamId)
    {
        foreach (var connection in _watching.Where(value => value.Value == streamId).Select(value => value.Key).ToArray())
            _watching.Remove(connection);
    }
}
