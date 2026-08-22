using Iridium.Protocol;
using Iridium.Server.Voice;

namespace Iridium.Tests;

public sealed class CommunityVoiceRoomServiceTests
{
    [Fact]
    public async Task FirstJoinStartsRoomAndAdditionalAndDuplicateJoinsPreserveStartTime()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        var service = new CommunityVoiceRoomService(clock, new TestMediaGateway());
        var communityId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        var first = await service.JoinAsync(communityId, channelId, accountId, "connection-a", "Skye",
            PublicPresence.Online, "Iridium", "Lounge");
        Assert.Equal(clock.GetUtcNow(), first.Room.StartedAt);
        Assert.Single(first.Room.Participants);

        clock.Advance(TimeSpan.FromMinutes(2));
        var duplicate = await service.JoinAsync(communityId, channelId, accountId, "connection-a", "Skye",
            PublicPresence.Online, "Iridium", "Lounge");
        Assert.True(duplicate.AlreadyJoined);
        Assert.Single(duplicate.Room.Participants);
        Assert.Equal(first.Room.StartedAt, duplicate.Room.StartedAt);

        var second = await service.JoinAsync(communityId, channelId, accountId, "connection-b", "Skye",
            PublicPresence.Online, "Iridium", "Lounge");
        Assert.Equal(2, second.Room.Participants.Count);
        Assert.Equal(2, second.Room.Participants.Select(value => value.ParticipantId).Distinct().Count());
        Assert.Equal(first.Room.StartedAt, second.Room.StartedAt);
    }

    [Fact]
    public async Task LeaveSwitchAndDisconnectStyleCleanupDestroyEmptyRooms()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        var media = new TestMediaGateway();
        var service = new CommunityVoiceRoomService(clock, media);
        var communityId = Guid.NewGuid();
        var firstChannel = Guid.NewGuid();
        var secondChannel = Guid.NewGuid();

        await service.JoinAsync(communityId, firstChannel, Guid.NewGuid(), "connection-a", "Skye",
            PublicPresence.Online, "Iridium", "Lounge");
        await service.JoinAsync(communityId, firstChannel, Guid.NewGuid(), "connection-b", "Alice",
            PublicPresence.Online, "Iridium", "Lounge");
        clock.Advance(TimeSpan.FromSeconds(30));
        var switched = await service.JoinAsync(communityId, secondChannel, Guid.NewGuid(), "connection-a", "Skye",
            PublicPresence.Online, "Iridium", "Studio");
        Assert.NotNull(switched.PreviousRoom);
        Assert.Single(switched.PreviousRoom!.Room!.Participants);
        Assert.Equal(secondChannel, service.RoomFor("connection-a")?.ChannelId);
        Assert.Equal(clock.GetUtcNow(), switched.Room.StartedAt);

        var remainingLeave = await service.LeaveAsync("connection-b");
        Assert.NotNull(remainingLeave);
        Assert.Null(remainingLeave!.Room);
        Assert.DoesNotContain(service.GetRooms(communityId), value => value.ChannelId == firstChannel);
        var lastLeave = await service.LeaveAsync("connection-a");
        Assert.Null(lastLeave!.Room);
        Assert.Empty(service.GetRooms(communityId));
        Assert.Equal(3, media.Left.Count);
    }

    [Fact]
    public async Task ParticipantMuteDeafenAndSpeakingStateIsTransientAndConnectionScoped()
    {
        var service = new CommunityVoiceRoomService(new TestTimeProvider(DateTimeOffset.UtcNow),
            new TestMediaGateway());
        var communityId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await service.JoinAsync(communityId, channelId, accountId, "device-one", "Skye",
            PublicPresence.Online, "Iridium", "Lounge");
        await service.JoinAsync(communityId, channelId, accountId, "device-two", "Skye",
            PublicPresence.Online, "Iridium", "Lounge");

        Assert.NotNull(service.SetSpeaking("device-one", true));
        Assert.Null(service.SetSpeaking("device-one", true));
        var deafened = await service.SetStateAsync("device-one", false, true);
        Assert.True(deafened!.Participant.Muted);
        Assert.True(deafened.Participant.Deafened);
        Assert.False(deafened.Participant.Speaking);
        Assert.Null(service.SetSpeaking("device-one", true));
        Assert.False(service.GetRooms(communityId).Single().Participants
            .Single(value => value.ParticipantId == "device-two").Muted);

        var speaking = service.SetSpeaking("device-two", true);
        Assert.True(speaking!.Participant.Speaking);
        Assert.Null(service.SetSpeaking("device-two", true));
        Assert.NotNull(service.SetSpeaking("device-two", false));
        Assert.Null(service.SetSpeaking("device-two", false));
        Assert.Equal(CommunityVoiceMediaStatus.MediaUnavailable, speaking.Participant.MediaStatus);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class TestMediaGateway : ICommunityVoiceMediaGateway
    {
        public CommunityVoiceMediaStatus Status => CommunityVoiceMediaStatus.MediaUnavailable;
        public int? MaximumParticipants => null;
        public List<string> Left { get; } = [];
        public ValueTask<CommunityVoiceMediaSessionDto> PrepareSessionAsync(Guid communityId, Guid channelId,
            string participantId, Guid accountId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CommunityVoiceMediaSessionDto(Status, "test"));
        public ValueTask ParticipantJoinedAsync(Guid communityId, Guid channelId, string participantId,
            Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ParticipantStateChangedAsync(Guid communityId, Guid channelId, string participantId,
            bool muted, bool deafened, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ParticipantLeftAsync(Guid communityId, Guid channelId, string participantId,
            CancellationToken cancellationToken = default)
        {
            Left.Add(participantId);
            return ValueTask.CompletedTask;
        }
    }
}
