using Iridium.Protocol;
using Iridium.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Voice;

/// <summary>
/// Boundary between authoritative Community room control and an embedded or remote SFU.
/// Implementations must transport media with WebRTC; SignalR is control/signaling only.
/// </summary>
public interface ICommunityVoiceMediaGateway
{
    CommunityVoiceMediaStatus Status { get; }
    int? MaximumParticipants { get; }
    ValueTask<CommunityVoiceMediaSessionDto> PrepareSessionAsync(Guid communityId, Guid channelId,
        string participantId, Guid accountId, CancellationToken cancellationToken = default);
    ValueTask ParticipantJoinedAsync(Guid communityId, Guid channelId, string participantId,
        Guid accountId, CancellationToken cancellationToken = default);
    ValueTask ParticipantStateChangedAsync(Guid communityId, Guid channelId, string participantId,
        bool muted, bool deafened, CancellationToken cancellationToken = default);
    ValueTask ParticipantLeftAsync(Guid communityId, Guid channelId, string participantId,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableCommunityVoiceMediaGateway : ICommunityVoiceMediaGateway
{
    public CommunityVoiceMediaStatus Status => CommunityVoiceMediaStatus.MediaUnavailable;
    public int? MaximumParticipants => null;
    public ValueTask<CommunityVoiceMediaSessionDto> PrepareSessionAsync(Guid communityId, Guid channelId,
        string participantId, Guid accountId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new CommunityVoiceMediaSessionDto(Status, "none"));
    public ValueTask ParticipantJoinedAsync(Guid communityId, Guid channelId, string participantId,
        Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ParticipantStateChangedAsync(Guid communityId, Guid channelId, string participantId,
        bool muted, bool deafened, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ParticipantLeftAsync(Guid communityId, Guid channelId, string participantId,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>
/// Temporary Development-only media provider. It advertises browser WebRTC peer signaling while
/// keeping the room/domain contract topology-neutral so it can be replaced by NodeSfu.
/// TODO: Remove temporary Community peer-mesh media once the embedded Node SFU is available.
/// </summary>
public sealed class DevelopmentPeerMeshCommunityVoiceMediaGateway(IOptions<MediaOptions> options,
    IHostEnvironment environment) : ICommunityVoiceMediaGateway
{
    public CommunityVoiceMediaStatus Status => CommunityVoiceMediaStatus.Connected;
    public int? MaximumParticipants => Math.Clamp(options.Value.DevelopmentCommunityPeerLimit, 2, 12);

    public ValueTask<CommunityVoiceMediaSessionDto> PrepareSessionAsync(Guid communityId, Guid channelId,
        string participantId, Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment() || !options.Value.EnableDevelopmentCommunityPeerMesh)
            return ValueTask.FromResult(new CommunityVoiceMediaSessionDto(
                CommunityVoiceMediaStatus.MediaUnavailable, "none"));
        return ValueTask.FromResult(new CommunityVoiceMediaSessionDto(Status, "development-peer-mesh",
            ParticipantId: participantId, DiagnosticsEnabled: true));
    }

    public ValueTask ParticipantJoinedAsync(Guid communityId, Guid channelId, string participantId,
        Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ParticipantStateChangedAsync(Guid communityId, Guid channelId, string participantId,
        bool muted, bool deafened, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ParticipantLeftAsync(Guid communityId, Guid channelId, string participantId,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
