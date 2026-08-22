using Iridium.Protocol;

namespace Iridium.Server.Calls;

public sealed class CallSession
{
    public required Guid Id { get; init; }
    public required CallKind Kind { get; init; }
    public required Guid? DirectConversationId { get; init; }
    public required Guid CallerAccountId { get; init; }
    public required CallState State { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public required Dictionary<Guid, CallParticipant> Participants { get; init; }
}

public sealed class CallParticipant
{
    public required Guid AccountId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsMuted { get; set; }
    public bool IsDeafened { get; set; }
    public bool IsSpeaking { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public CallConnectionState ConnectionState { get; set; } = CallConnectionState.New;
    public required DateTimeOffset LastSignalingAt { get; set; }
    public string? SignalingConnectionId { get; set; }
}
