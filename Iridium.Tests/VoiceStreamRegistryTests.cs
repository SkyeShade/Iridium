using Iridium.Protocol;
using Iridium.Server.Voice;

namespace Iridium.Tests;

public sealed class VoiceStreamRegistryTests
{
    [Fact]
    public void ParticipantCanPublishOneScreenWhileMultipleOwnersRemainRepresentable()
    {
        var registry = new VoiceStreamRegistry(TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var first = registry.Publish(VoiceMediaSessionKind.CommunityVoice, sessionId, alice, "Alice", "a", Request());
        var bobStream = registry.Publish(VoiceMediaSessionKind.CommunityVoice, sessionId, bob, "Bob", "b", Request());
        var replacement = registry.Publish(VoiceMediaSessionKind.CommunityVoice, sessionId, alice, "Alice", "a", Request());

        Assert.Null(first.Replaced);
        Assert.NotNull(replacement.Replaced);
        Assert.Equal(first.Stream.StreamId, replacement.Replaced!.StreamId);
        Assert.Equal(2, registry.Get(VoiceMediaSessionKind.CommunityVoice, sessionId).Count);
        Assert.Contains(bobStream.Stream, registry.Get(VoiceMediaSessionKind.CommunityVoice, sessionId));
    }

    [Fact]
    public void WatchingIsIndependentAndPublisherDisconnectEndsOnlyOwnedStreams()
    {
        var registry = new VoiceStreamRegistry(TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var stream = registry.Publish(VoiceMediaSessionKind.DirectCall, sessionId, Guid.NewGuid(), "Alice", "a", Request()).Stream;

        Assert.True(registry.Watch("viewer", VoiceMediaSessionKind.DirectCall, sessionId, stream.StreamId));
        registry.StopWatching("viewer", stream.StreamId);
        var ended = registry.RemoveConnection("a", "ParticipantDisconnected");

        Assert.Single(ended);
        Assert.Empty(registry.Get(VoiceMediaSessionKind.DirectCall, sessionId));
    }

    [Fact]
    public void CommunityAllIncludesShareScreenWithoutChangingProfilePresetLimits()
    {
        Assert.True((CommunityPermission.All & CommunityPermission.ShareScreen) != 0);
        Assert.Equal(10, ProfileAvatarLimits.MaximumPresets);
        Assert.Equal(4, ProfileBannerLimits.MaximumPresets);
    }

    [Fact]
    public void UpdatingShareAudioPreservesStreamIdentityAndWatcher()
    {
        var registry = new VoiceStreamRegistry(TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var stream = registry.Publish(VoiceMediaSessionKind.DirectCall, sessionId, Guid.NewGuid(),
            "Alice", "publisher", Request() with { HasAudio = false }).Stream;
        Assert.True(registry.Watch("viewer", VoiceMediaSessionKind.DirectCall, sessionId, stream.StreamId));

        var updated = registry.Update(VoiceMediaSessionKind.DirectCall, sessionId, stream.StreamId,
            "publisher", hasAudio: true);

        Assert.NotNull(updated);
        Assert.Equal(stream.StreamId, updated.StreamId);
        Assert.Equal(stream.MediaStreamId, updated.MediaStreamId);
        Assert.True(updated.HasAudio);
        Assert.False(registry.Watch("missing", VoiceMediaSessionKind.DirectCall, sessionId, Guid.NewGuid()));
    }

    private static PublishVoiceStreamRequest Request() => new(Guid.NewGuid(),
        VoicePublishedStreamKind.ScreenShare, true, $"browser-{Guid.NewGuid():N}");
}
