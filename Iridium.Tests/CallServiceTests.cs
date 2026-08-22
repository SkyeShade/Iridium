using Iridium.Protocol;
using Iridium.Server.Calls;
using Iridium.Server.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;

namespace Iridium.Tests;

public sealed class CallServiceTests
{
    private const string CallerConnection = "caller-connection";
    private const string CalleeConnection = "callee-connection";

    [Fact]
    public void DirectConversationAllowsOnlyOneLiveCallAndAuthorizedParticipants()
    {
        var service = CreateService();
        var conversationId = Guid.NewGuid();
        var caller = Guid.NewGuid();
        var callee = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        var call = service.CreateDirect(conversationId, caller, "Caller", callee, "Callee", CallerConnection);
        Assert.Equal(CallState.Ringing, call.State);
        Assert.Throws<HubException>(() => service.CreateDirect(conversationId, caller, "Caller", callee, "Callee", CallerConnection));
        Assert.Throws<HubException>(() => service.CreateDirect(Guid.NewGuid(), caller, "Caller", Guid.NewGuid(), "Third", CallerConnection));
        Assert.Throws<HubException>(() => service.RequireParticipant(call.Id, outsider, CallState.Ringing));

        var accepted = service.Accept(call.Id, callee, CalleeConnection);
        Assert.Equal(CallState.Active, accepted.State);
        Assert.NotNull(accepted.AcceptedAt);
        Assert.Equal(accepted.AcceptedAt, accepted.Participants.Single(value => value.AccountId == callee).JoinedAt);
        Assert.Throws<HubException>(() => service.Reject(call.Id, callee));
        Assert.Equal(CallState.Ended, service.HangUp(call.Id, caller).State);

        var next = service.CreateDirect(conversationId, callee, "Callee", caller, "Caller", CalleeConnection);
        Assert.Equal(CallState.Ringing, next.State);
    }

    [Fact]
    public void LifecycleRolesAndSignalingDirectionAreEnforced()
    {
        var service = CreateService();
        var caller = Guid.NewGuid();
        var callee = Guid.NewGuid();
        var call = service.CreateDirect(Guid.NewGuid(), caller, "Caller", callee, "Callee", CallerConnection);
        var media = new DirectWebRtcMediaService(service, Options.Create(new MediaOptions()));
        var offer = new WebRtcSessionDescription("offer", "safe-test-sdp");
        var answer = new WebRtcSessionDescription("answer", "safe-test-sdp");
        var negotiationId = Guid.NewGuid();
        Assert.Throws<HubException>(() => media.AuthorizeOffer(call.Id, caller, CallerConnection, negotiationId,
            WebRtcNegotiationKind.Initial, offer));
        Assert.Throws<HubException>(() => service.Accept(call.Id, caller, CallerConnection));

        service.Accept(call.Id, callee, CalleeConnection);
        var earlyIce = media.AuthorizeIceCandidate(call.Id, caller, CallerConnection, negotiationId,
            new WebRtcIceCandidate("candidate:early", "audio", 0, "fragment"));
        Assert.True(earlyIce.ShouldForward);

        var offerRoute = media.AuthorizeOffer(call.Id, caller, CallerConnection, negotiationId,
            WebRtcNegotiationKind.Initial, offer);
        Assert.Equal(callee, offerRoute.TargetAccountId);
        Assert.Equal(CalleeConnection, offerRoute.TargetConnectionId);
        Assert.True(offerRoute.ShouldForward);
        Assert.Equal("duplicate-offer", media.AuthorizeOffer(call.Id, caller, CallerConnection, negotiationId,
            WebRtcNegotiationKind.Initial, offer).IgnoreReason);
        Assert.Throws<HubException>(() => media.AuthorizeOffer(call.Id, callee, CalleeConnection, Guid.NewGuid(),
            WebRtcNegotiationKind.Initial, offer));
        Assert.Throws<HubException>(() => media.AuthorizeAnswer(call.Id, callee, "other-callee-connection", negotiationId, answer));

        var answerRoute = media.AuthorizeAnswer(call.Id, callee, CalleeConnection, negotiationId, answer);
        Assert.Equal(caller, answerRoute.TargetAccountId);
        Assert.Equal(CallerConnection, answerRoute.TargetConnectionId);
        Assert.True(answerRoute.ShouldForward);
        var duplicateAnswer = media.AuthorizeAnswer(call.Id, callee, CalleeConnection, negotiationId, answer);
        Assert.False(duplicateAnswer.ShouldForward);
        Assert.Equal("duplicate-answer", duplicateAnswer.IgnoreReason);
        var staleAnswer = media.AuthorizeAnswer(call.Id, callee, CalleeConnection, Guid.NewGuid(), answer);
        Assert.False(staleAnswer.ShouldForward);
        Assert.Equal("stale-negotiation", staleAnswer.IgnoreReason);
        Assert.Throws<HubException>(() => media.AuthorizeAnswer(call.Id, caller, CallerConnection, negotiationId, answer));

        var callerNegotiationId = Guid.NewGuid();
        var callerRenegotiation = media.AuthorizeOffer(call.Id, caller, CallerConnection, callerNegotiationId,
            WebRtcNegotiationKind.Renegotiation, offer);
        Assert.True(callerRenegotiation.ShouldForward);
        Assert.Equal(WebRtcNegotiationKind.Renegotiation, callerRenegotiation.NegotiationKind);
        var calleeNegotiationId = Guid.NewGuid();
        var calleeRenegotiation = media.AuthorizeOffer(call.Id, callee, CalleeConnection, calleeNegotiationId,
            WebRtcNegotiationKind.Renegotiation, offer);
        Assert.True(calleeRenegotiation.ShouldForward);
        Assert.Equal(caller, calleeRenegotiation.TargetAccountId);
        Assert.Throws<HubException>(() => media.AuthorizeOffer(call.Id, Guid.NewGuid(), "outsider-connection",
            Guid.NewGuid(), WebRtcNegotiationKind.Renegotiation, offer));
        Assert.Throws<HubException>(() => media.AuthorizeOffer(call.Id, callee, "other-callee-connection",
            Guid.NewGuid(), WebRtcNegotiationKind.Renegotiation, offer));
        Assert.True(media.AuthorizeAnswer(call.Id, callee, CalleeConnection, callerNegotiationId, answer).ShouldForward);
        Assert.True(media.AuthorizeAnswer(call.Id, caller, CallerConnection, calleeNegotiationId, answer).ShouldForward);
        Assert.Throws<HubException>(() => media.AuthorizeIceCandidate(call.Id, Guid.NewGuid(), "outsider-connection", negotiationId,
            new WebRtcIceCandidate("candidate", null, null, null)));
        var speaking = service.SetParticipantSpeaking(call.Id, caller, true);
        Assert.True(speaking.IsSpeaking);
        Assert.True(service.CurrentFor(caller, CallerConnection)!.Participants.Single(value => value.AccountId == caller).IsSpeaking);
        Assert.Null(service.CurrentFor(caller, "other-caller-connection"));
        Assert.Throws<HubException>(() => service.SetParticipantSpeaking(call.Id, Guid.NewGuid(), true));
    }

    [Fact]
    public void AcceptAtomicallyClaimsOneCalleeConnectionAndRoutesOnlyBetweenSelectedEndpoints()
    {
        var service = CreateService();
        var caller = Guid.NewGuid();
        var callee = Guid.NewGuid();
        var call = service.CreateDirect(Guid.NewGuid(), caller, "Caller", callee, "Callee", "A1");

        service.Accept(call.Id, callee, "B1");

        Assert.Equal("B1", service.RequireSignalingRoute(call.Id, caller, "A1", CallState.Active).TargetConnectionId);
        Assert.Equal("A1", service.RequireSignalingRoute(call.Id, callee, "B1", CallState.Active).TargetConnectionId);
        Assert.Throws<HubException>(() => service.Accept(call.Id, callee, "B2"));
        Assert.Throws<HubException>(() => service.RequireSignalingRoute(call.Id, caller, "A2", CallState.Active));
        Assert.Throws<HubException>(() => service.RequireSignalingRoute(call.Id, callee, "B2", CallState.Active));
        Assert.Null(service.CurrentFor(caller, "A2"));
        Assert.Null(service.CurrentFor(callee, "B2"));
    }

    [Fact]
    public void SelectedConnectionDisconnectEndsCallWithoutSubstitutingAnotherAccountConnection()
    {
        var service = CreateService();
        var caller = Guid.NewGuid();
        var callee = Guid.NewGuid();
        var call = service.CreateDirect(Guid.NewGuid(), caller, "Caller", callee, "Callee", "A1");
        service.Accept(call.Id, callee, "B1");

        Assert.Null(service.DisconnectSignaling("A2"));
        var loss = Assert.IsType<CallConnectionLoss>(service.DisconnectSignaling("B1"));

        Assert.Equal(CallState.Ended, loss.Call.State);
        Assert.Equal(caller, loss.RemainingAccountId);
        Assert.Equal("A1", loss.RemainingConnectionId);
        Assert.Null(service.CurrentFor(caller, "A1"));
    }

    [Fact]
    public void RingTimeoutCancelsAndClosesParticipants()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-21T00:00:00Z"));
        var service = CreateService(clock, 30);
        var call = service.CreateDirect(Guid.NewGuid(), Guid.NewGuid(), "Caller", Guid.NewGuid(), "Callee", CallerConnection);
        clock.Advance(TimeSpan.FromSeconds(31));

        var expired = Assert.Single(service.ExpireRingingCalls());
        Assert.Equal(call.Id, expired.Id);
        Assert.Equal(CallState.Cancelled, expired.State);
        Assert.All(expired.Participants, value => Assert.Equal(CallConnectionState.Closed, value.ConnectionState));
        Assert.Null(service.CurrentFor(call.CallerAccountId, CallerConnection));
    }

    [Fact]
    public async Task DirectCallAuthorizationRequiresConversationMembershipAndRespectsBlockingInEitherDirection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var caller = new NodeAccount { Id = Guid.NewGuid(), Username = "caller", DisplayName = "Caller", PasswordHash = "x", CreatedAt = now };
        var callee = new NodeAccount { Id = Guid.NewGuid(), Username = "callee", DisplayName = "Callee", PasswordHash = "x", CreatedAt = now };
        var outsider = new NodeAccount { Id = Guid.NewGuid(), Username = "outsider", DisplayName = "Outsider", PasswordHash = "x", CreatedAt = now };
        var conversation = new DirectConversation { Id = Guid.NewGuid(), ParticipantAAccountId = caller.Id,
            ParticipantBAccountId = callee.Id, ParticipantAAccount = caller, ParticipantBAccount = callee, CreatedAt = now };
        db.AddRange(caller, callee, outsider, conversation);
        await db.SaveChangesAsync();
        var authorization = new DirectCallAuthorizationService(db);

        var parties = await authorization.AuthorizeStartAsync(conversation.Id, caller.Id);
        Assert.Equal(callee.Id, parties.CalleeId);
        await Assert.ThrowsAsync<HubException>(() => authorization.AuthorizeStartAsync(conversation.Id, outsider.Id));

        db.AccountBlocks.Add(new AccountBlock { BlockingAccountId = callee.Id, BlockedAccountId = caller.Id,
            BlockingAccount = callee, BlockedAccount = caller, CreatedAt = now });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<HubException>(() => authorization.AuthorizeStartAsync(conversation.Id, caller.Id));
        await Assert.ThrowsAsync<HubException>(() => authorization.AuthorizeStartAsync(conversation.Id, callee.Id));
    }

    [Fact]
    public void SignificantSignalingLossEndsActiveCallButRecentHeartbeatKeepsItAlive()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-21T00:00:00Z"));
        var service = new CallService(Options.Create(new MediaOptions { SignalingLossTimeoutSeconds = 45 }),
            clock, NullLogger<CallService>.Instance);
        var caller = Guid.NewGuid();
        var callee = Guid.NewGuid();
        var call = service.CreateDirect(Guid.NewGuid(), caller, "Caller", callee, "Callee", CallerConnection);
        service.Accept(call.Id, callee, CalleeConnection);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(service.ExpireAbandonedActiveCalls());
        service.SetParticipantState(call.Id, caller, false, false, CallConnectionState.Connected);
        service.SetParticipantState(call.Id, callee, false, false, CallConnectionState.Connected);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty(service.ExpireAbandonedActiveCalls());
        clock.Advance(TimeSpan.FromSeconds(26));
        Assert.Single(service.ExpireAbandonedActiveCalls());
        Assert.Null(service.CurrentFor(caller, CallerConnection));
    }

    private static CallService CreateService(TimeProvider? clock = null, int timeoutSeconds = 30) => new(
        Options.Create(new MediaOptions { RingTimeoutSeconds = timeoutSeconds }),
        clock ?? TimeProvider.System,
        NullLogger<CallService>.Instance);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
