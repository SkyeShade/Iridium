using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class WebRtcSignalingGuardTests
{
    [Fact]
    public void DuplicateAnswerIsIgnoredAfterFirstApplication()
    {
        var negotiationId = Guid.NewGuid();

        Assert.Equal(RemoteAnswerDisposition.Apply,
            WebRtcSignalingGuard.ClassifyAnswer(negotiationId, negotiationId, false, "have-local-offer"));
        Assert.Equal(RemoteAnswerDisposition.Duplicate,
            WebRtcSignalingGuard.ClassifyAnswer(negotiationId, negotiationId, true, "stable"));
    }

    [Fact]
    public void StaleAnswerCannotReachANewerPeerGeneration()
    {
        var oldNegotiationId = Guid.NewGuid();
        var currentNegotiationId = Guid.NewGuid();

        Assert.Equal(RemoteAnswerDisposition.StaleNegotiation,
            WebRtcSignalingGuard.ClassifyAnswer(currentNegotiationId, oldNegotiationId, false, "have-local-offer"));
    }

    [Theory]
    [InlineData("stable", (int)RemoteAnswerDisposition.AlreadyApplied)]
    [InlineData("have-remote-offer", (int)RemoteAnswerDisposition.InvalidState)]
    [InlineData("closed", (int)RemoteAnswerDisposition.InvalidState)]
    public void CurrentAnswerRequiresAnOutstandingLocalOffer(string state, int expected)
    {
        var negotiationId = Guid.NewGuid();
        Assert.Equal((RemoteAnswerDisposition)expected,
            WebRtcSignalingGuard.ClassifyAnswer(negotiationId, negotiationId, false, state));
    }
}
