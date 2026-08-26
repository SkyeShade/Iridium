using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class ActiveVoiceSessionCoordinatorTests
{
    [Fact]
    public async Task AnswerIncomingCallLeavesCommunityBeforeAcceptingDirectCall()
    {
        var order = new List<string>();
        var direct = new FakeDirect(order) { IncomingCall = Incoming() };
        var community = new FakeCommunity(order) { CurrentRoom = Room(Guid.NewGuid()) };
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, community);

        await coordinator.AcceptIncomingDirectAsync();

        Assert.Equal(["community-leave", "direct-accept"], order);
        Assert.Null(community.CurrentRoom);
        Assert.Equal(ActiveVoiceSessionKind.DirectCall, coordinator.Current?.Kind);
    }

    [Fact]
    public async Task IncomingAndDeclinedCallDoNotSwitchExistingCommunitySession()
    {
        var order = new List<string>();
        var room = Room(Guid.NewGuid());
        var direct = new FakeDirect(order) { IncomingCall = Incoming() };
        var community = new FakeCommunity(order) { CurrentRoom = room };
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, community);

        Assert.Equal(ActiveVoiceSessionKind.CommunityVoiceChannel, coordinator.Current?.Kind);
        Assert.NotNull(coordinator.IncomingCall);
        Assert.Empty(order);
        await coordinator.DeclineIncomingDirectAsync();

        Assert.Equal(["direct-decline"], order);
        Assert.Same(room, community.CurrentRoom);
        Assert.Equal(ActiveVoiceSessionKind.CommunityVoiceChannel, coordinator.Current?.Kind);
    }

    [Fact]
    public async Task JoiningCommunityEndsDirectCallBeforeJoiningAndChannelSwitchLeavesOldRoom()
    {
        var order = new List<string>();
        var direct = new FakeDirect(order) { CurrentCall = DirectCall() };
        var community = new FakeCommunity(order);
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, community);
        var communityId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await coordinator.JoinCommunityAsync(communityId, first);
        Assert.Equal(["direct-hangup", $"community-join:{first}"], order);
        Assert.Null(direct.CurrentCall);
        Assert.Equal(first, coordinator.Current?.ChannelId);

        order.Clear();
        await coordinator.JoinCommunityAsync(communityId, second);
        Assert.Equal(["community-leave", $"community-join:{second}"], order);
        Assert.Equal(second, coordinator.Current?.ChannelId);
    }

    [Fact]
    public async Task SharedControlsAndDisconnectTargetOnlyTheActiveSessionKind()
    {
        var order = new List<string>();
        var direct = new FakeDirect(order);
        var community = new FakeCommunity(order) { CurrentRoom = Room(Guid.NewGuid()) };
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, community);

        await coordinator.ToggleMuteAsync();
        await coordinator.ToggleDeafenAsync();
        Assert.True(coordinator.Current!.Muted);
        Assert.True(coordinator.Current.Deafened);
        await coordinator.LeaveCurrentVoiceSessionAsync("panel disconnect");
        Assert.Null(coordinator.Current);

        direct.CurrentCall = DirectCall();
        await coordinator.ToggleMuteAsync();
        await coordinator.LeaveCurrentVoiceSessionAsync("panel disconnect");
        Assert.Contains("direct-hangup", order);
        Assert.Null(coordinator.Current);
    }

    [Fact]
    public async Task ScreenShareAndWatchControlsTargetOnlyActiveSessionWithoutCreatingAnotherVoiceSession()
    {
        var order = new List<string>();
        var direct = new FakeDirect(order);
        var community = new FakeCommunity(order) { CurrentRoom = Room(Guid.NewGuid()) };
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, community);
        var streamId = Guid.NewGuid();

        await coordinator.StartScreenShareAsync();
        await coordinator.WatchStreamAsync(streamId);
        Assert.Equal(StreamViewerMode.Full, coordinator.ViewerMode);
        coordinator.MinimizeWatchedStream();
        Assert.Equal(StreamViewerMode.Floating, coordinator.ViewerMode);
        coordinator.ShowWatchedStreamFull();
        Assert.Equal(StreamViewerMode.Full, coordinator.ViewerMode);
        await coordinator.StopWatchingAsync();
        Assert.Equal(StreamViewerMode.None, coordinator.ViewerMode);
        await coordinator.StopScreenShareAsync();

        Assert.Equal(["community-share", $"community-watch:{streamId}", "community-stop-watch",
            "community-stop-share:UserStoppedInIridium"], order);
        Assert.Equal(ActiveVoiceSessionKind.CommunityVoiceChannel, coordinator.Current?.Kind);
    }

    [Fact]
    public void FloatingViewerDefinesExactlySixSnapPositions()
    {
        Assert.Equal(6, Enum.GetValues<FloatingStreamPosition>().Length);
        Assert.Contains(FloatingStreamPosition.TopMiddle, Enum.GetValues<FloatingStreamPosition>());
        Assert.Contains(FloatingStreamPosition.BottomMiddle, Enum.GetValues<FloatingStreamPosition>());
    }

    [Fact]
    public async Task WatchingOwnPublishedStreamDefaultsItsScreenAudioToMuted()
    {
        var direct = new FakeDirect([]) { CurrentCall = DirectCall() };
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, new FakeCommunity([]));
        var streamId = Guid.NewGuid();

        await coordinator.WatchStreamAsync(streamId);

        Assert.Equal(StreamViewerMode.Full, coordinator.ViewerMode);
        Assert.True(coordinator.IsStreamAudioMuted(streamId));
    }

    [Fact]
    public async Task IdlePreferencesSeedNewSessionsAndSharedControlsStaySynchronized()
    {
        var direct = new FakeDirect([]);
        var community = new FakeCommunity([]);
        var preferences = new LocalVoicePreferenceService(new MemoryLocalStore());
        await using var coordinator = new ActiveVoiceSessionCoordinator(direct, community, preferences);
        await coordinator.SetPreferenceScopeAsync("node-a", Guid.NewGuid());

        await coordinator.ToggleMuteAsync();
        Assert.True(coordinator.PreferredMuted);
        Assert.True(direct.IsMuted);
        Assert.True(community.Muted);

        await coordinator.StartDirectAsync(Conversation(Guid.NewGuid(), Guid.NewGuid(), 0));
        Assert.True(coordinator.Current!.Muted);

        await coordinator.ToggleDeafenAsync();
        Assert.True(coordinator.PreferredDeafened);
        Assert.True(coordinator.EffectiveMuted);
        Assert.True(direct.IsDeafened);

        await coordinator.ToggleMuteAsync();
        Assert.False(coordinator.PreferredMuted);
        Assert.True(coordinator.Current.Muted);
        await coordinator.ToggleDeafenAsync();
        Assert.False(coordinator.Current.Muted);
        Assert.False(coordinator.Current.Deafened);
    }

    [Fact]
    public void RingingCallerProjectionReusesUnreadEntryAndIncludesCallOnlyEntryOnce()
    {
        var caller = Guid.NewGuid();
        var unread = Conversation(Guid.NewGuid(), caller, 3);
        var duplicate = unread with { LastMessageAt = unread.LastMessageAt?.AddSeconds(-1) };
        Assert.Single(DirectNotificationProjection.Build([unread, duplicate], caller));

        var callOnly = Conversation(Guid.NewGuid(), caller, 0);
        Assert.Single(DirectNotificationProjection.Build([callOnly], caller));
        Assert.Empty(DirectNotificationProjection.Build([callOnly], null));
    }

    private static IncomingCallEvent Incoming() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30));
    private static CallSessionDto DirectCall() => new(Guid.NewGuid(), CallKind.DirectVoice, Guid.NewGuid(), Guid.NewGuid(),
        CallState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), [] , DateTimeOffset.UtcNow);
    private static ActiveVoiceRoomDto Room(Guid channelId) => new(Guid.NewGuid(), channelId, DateTimeOffset.UtcNow,
        [], "Community", "Lounge");
    private static DirectConversationDto Conversation(Guid id, Guid accountId, int unread) => new(id,
        new(accountId, "alice", "Alice", null, null, PublicPresence.Online), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, unread);

    private sealed class FakeDirect(List<string> order) : IDirectVoiceSession
    {
        public event Action? Changed;
        public CallSessionDto? CurrentCall { get; set; }
        public IncomingCallEvent? IncomingCall { get; set; }
        public Guid? AccountId { get; } = Guid.NewGuid();
        public bool IsMuted { get; private set; }
        public bool IsDeafened { get; private set; }
        public CallConnectionState MediaConnectionState => CurrentCall is null ? CallConnectionState.Closed : CallConnectionState.Connected;
        public IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams { get; } = [];
        public PublishedVoiceStreamDto? WatchedStream { get; private set; }
        public Task StartAsync(DirectConversationDto conversation, CancellationToken cancellationToken = default)
        { order.Add("direct-start"); CurrentCall = DirectCall(); Changed?.Invoke(); return Task.CompletedTask; }
        public Task AcceptAsync(CancellationToken cancellationToken = default)
        { order.Add("direct-accept"); IncomingCall = null; CurrentCall = DirectCall(); Changed?.Invoke(); return Task.CompletedTask; }
        public Task DeclineAsync(CancellationToken cancellationToken = default)
        { order.Add("direct-decline"); IncomingCall = null; Changed?.Invoke(); return Task.CompletedTask; }
        public Task HangUpAsync(CancellationToken cancellationToken = default)
        { order.Add("direct-hangup"); CurrentCall = null; Changed?.Invoke(); return Task.CompletedTask; }
        public Task ToggleMuteAsync(CancellationToken cancellationToken = default)
        { IsMuted = !IsMuted; Changed?.Invoke(); return Task.CompletedTask; }
        public Task ToggleDeafenAsync(CancellationToken cancellationToken = default)
        { IsDeafened = !IsDeafened; Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetLocalVoiceStateAsync(bool muted, bool deafened, CancellationToken cancellationToken = default)
        { IsMuted = muted; IsDeafened = deafened; Changed?.Invoke(); return Task.CompletedTask; }
        public Task StartScreenShareAsync(CancellationToken cancellationToken = default)
        { order.Add("direct-share"); return Task.CompletedTask; }
        public Task SwitchScreenShareAsync(CancellationToken cancellationToken = default)
        { order.Add("direct-switch-share"); return Task.CompletedTask; }
        public Task StopScreenShareAsync(string reason = "UserStoppedInIridium", CancellationToken cancellationToken = default)
        { order.Add($"direct-stop-share:{reason}"); return Task.CompletedTask; }
        public Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
        { order.Add($"direct-watch:{streamId}"); WatchedStream = TestStream(streamId, AccountId!.Value); Changed?.Invoke(); return Task.CompletedTask; }
        public Task StopWatchingAsync(CancellationToken cancellationToken = default)
        { order.Add("direct-stop-watch"); WatchedStream = null; Changed?.Invoke(); return Task.CompletedTask; }
        public Task AttachWatchedStreamAsync(string elementId, int volumePercent = 100, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStreamAudioVolumeAsync(string elementId, int volumePercent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeCommunity(List<string> order) : ICommunityVoiceControlSession
    {
        public event Action? Changed;
        public ActiveVoiceRoomDto? CurrentRoom { get; set; }
        public CommunityVoiceMediaSessionDto? MediaSession { get; } = new(CommunityVoiceMediaStatus.MediaUnavailable, "test");
        public bool Muted { get; private set; }
        public bool Deafened { get; private set; }
        public IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams { get; } = [];
        public PublishedVoiceStreamDto? WatchedStream { get; private set; }
        public Task JoinAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default)
        { order.Add($"community-join:{channelId}"); CurrentRoom = new(communityId, channelId, DateTimeOffset.UtcNow, [], "Community", "Voice"); Changed?.Invoke(); return Task.CompletedTask; }
        public Task LeaveAsync(CancellationToken cancellationToken = default)
        { order.Add("community-leave"); CurrentRoom = null; Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        { Muted = muted; Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default)
        { Deafened = deafened; Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetLocalVoiceStateAsync(bool muted, bool deafened, CancellationToken cancellationToken = default)
        { Muted = muted; Deafened = deafened; Changed?.Invoke(); return Task.CompletedTask; }
        public Task StartScreenShareAsync(CancellationToken cancellationToken = default)
        { order.Add("community-share"); return Task.CompletedTask; }
        public Task SwitchScreenShareAsync(CancellationToken cancellationToken = default)
        { order.Add("community-switch-share"); return Task.CompletedTask; }
        public Task StopScreenShareAsync(string reason = "UserStoppedInIridium", CancellationToken cancellationToken = default)
        { order.Add($"community-stop-share:{reason}"); return Task.CompletedTask; }
        public Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
        { order.Add($"community-watch:{streamId}"); WatchedStream = TestStream(streamId, Guid.NewGuid()); Changed?.Invoke(); return Task.CompletedTask; }
        public Task StopWatchingAsync(CancellationToken cancellationToken = default)
        { order.Add("community-stop-watch"); WatchedStream = null; Changed?.Invoke(); return Task.CompletedTask; }
        public Task AttachWatchedStreamAsync(string elementId, int volumePercent = 100, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStreamAudioVolumeAsync(string elementId, int volumePercent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class MemoryLocalStore : ILocalVoicePreferenceStore
    {
        private readonly Dictionary<LocalVoicePreferenceScope, LocalVoicePreference> _values = [];
        public Task<LocalVoicePreference?> LoadAsync(LocalVoicePreferenceScope scope,
            CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(scope));
        public Task SaveAsync(LocalVoicePreferenceScope scope, LocalVoicePreference preference,
            CancellationToken cancellationToken = default)
        { _values[scope] = preference; return Task.CompletedTask; }
    }


    private static PublishedVoiceStreamDto TestStream(Guid streamId, Guid ownerAccountId) =>
        new(streamId, VoiceMediaSessionKind.CommunityVoice, Guid.NewGuid(), ownerAccountId, "participant", "Skye",
            VoicePublishedStreamKind.ScreenShare, true, "media-stream", DateTimeOffset.UtcNow);
}
