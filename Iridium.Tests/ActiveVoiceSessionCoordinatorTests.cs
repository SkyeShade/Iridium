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
    }

    private sealed class FakeCommunity(List<string> order) : ICommunityVoiceControlSession
    {
        public event Action? Changed;
        public ActiveVoiceRoomDto? CurrentRoom { get; set; }
        public CommunityVoiceMediaSessionDto? MediaSession { get; } = new(CommunityVoiceMediaStatus.MediaUnavailable, "test");
        public bool Muted { get; private set; }
        public bool Deafened { get; private set; }
        public Task JoinAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default)
        { order.Add($"community-join:{channelId}"); CurrentRoom = new(communityId, channelId, DateTimeOffset.UtcNow, [], "Community", "Voice"); Changed?.Invoke(); return Task.CompletedTask; }
        public Task LeaveAsync(CancellationToken cancellationToken = default)
        { order.Add("community-leave"); CurrentRoom = null; Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        { Muted = muted; Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default)
        { Deafened = deafened; Changed?.Invoke(); return Task.CompletedTask; }
    }
}
