using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class TypingIndicatorTests
{
    private static readonly TypingConversationDto General =
        new(TypingConversationKind.CommunityChannel, Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public void DisplayFormatsZeroOneTwoThreeAndLongNames()
    {
        Assert.Null(TypingIndicatorState.Format([]));
        Assert.Equal("Skye is typing...", TypingIndicatorState.Format(["Skye"]));
        Assert.Equal("Skye and Alice are typing...", TypingIndicatorState.Format(["Skye", "Alice"]));
        Assert.Equal("Several people are typing...", TypingIndicatorState.Format(["Skye", "Alice", "Bob"]));
        Assert.Equal("Several people are typing...", TypingIndicatorState.Format(
            [new string('A', 30), new string('B', 30)]));
    }

    [Fact]
    public void StateExcludesSelfScopesConversationsAndOrdersMostRecentFirst()
    {
        var state = new TypingIndicatorState();
        var local = Guid.NewGuid();
        var skye = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var random = General with { ConversationId = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;

        Assert.False(state.Apply(Activity(General, local, "Me", now), local, now));
        state.Apply(Activity(General, skye, "Skye", now), local, now);
        state.Apply(Activity(General, alice, "Alice", now.AddSeconds(1)), local, now.AddSeconds(1));
        state.Apply(Activity(random, Guid.NewGuid(), "Random", now), local, now);

        Assert.Equal("Alice and Skye are typing...", state.TextFor(General));
        Assert.Equal("Random is typing...", state.TextFor(random));
    }

    [Fact]
    public void ActivityRefreshesTimeoutAndStopRemovesImmediately()
    {
        var state = new TypingIndicatorState();
        var account = Guid.NewGuid();
        var session = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        state.Apply(Activity(General, account, "Skye", started, session), null, started);

        Assert.False(state.Prune(started.AddSeconds(9)));
        state.Apply(Activity(General, account, "Skye", started.AddSeconds(9), session), null,
            started.AddSeconds(9));
        Assert.False(state.Prune(started.AddSeconds(18)));
        Assert.True(state.Prune(started.AddSeconds(19.001)));
        Assert.Null(state.TextFor(General));

        state.Apply(Activity(General, account, "Skye", started.AddSeconds(20), session), null,
            started.AddSeconds(20));
        Assert.True(state.Apply(Activity(General, account, "Skye", started.AddSeconds(21), session, false),
            null, started.AddSeconds(21)));
        Assert.Null(state.TextFor(General));
    }

    [Fact]
    public void RepeatedKeystrokesAreThrottledUntilFourSecondHeartbeat()
    {
        var started = DateTimeOffset.UtcNow;
        Assert.True(TypingActivityTiming.ShouldBroadcast(false, default, started));
        Assert.False(TypingActivityTiming.ShouldBroadcast(true, started, started.AddSeconds(3.999)));
        Assert.True(TypingActivityTiming.ShouldBroadcast(true, started, started.AddSeconds(4)));
    }

    [Fact]
    public void MultipleClientSessionsCollapseToOneAccountAndStopIndependently()
    {
        var state = new TypingIndicatorState();
        var account = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        state.Apply(Activity(General, account, "Skye", now, first), null, now);
        state.Apply(Activity(General, account, "Skye", now.AddSeconds(1), second), null, now.AddSeconds(1));

        Assert.Equal("Skye is typing...", state.TextFor(General));
        state.Apply(Activity(General, account, "Skye", now.AddSeconds(2), first, false), null, now.AddSeconds(2));
        Assert.Equal("Skye is typing...", state.TextFor(General));
        state.Apply(Activity(General, account, "Skye", now.AddSeconds(3), second, false), null, now.AddSeconds(3));
        Assert.Null(state.TextFor(General));
    }

    [Fact]
    public void ContractsAndUiKeepTypingEphemeralPrivateAndLayoutStable()
    {
        Assert.DoesNotContain(typeof(SetTypingActivityRequest).GetProperties(), property =>
            property.Name.Contains("Content", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Draft", StringComparison.OrdinalIgnoreCase));
        var root = FindRepositoryRoot();
        var composer = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageComposer.razor"));
        var indicator = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "TypingIndicatorLine.razor.css"));
        var server = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Hubs", "ChatHub.cs"));

        Assert.Contains("TypingActivityChanged.InvokeAsync(_content.Length > 0)", composer);
        Assert.Contains("ApplySnapshot(snapshot, scheduleDraft: false)", composer);
        Assert.Contains("height:1.15rem", indicator);
        Assert.Contains("font-size:.8rem", indicator);
        Assert.Contains("color:var(--text-muted)", indicator);
        Assert.Contains("white-space:nowrap", indicator);
        Assert.Contains("CommunityPermission.ViewChannels | CommunityPermission.SendMessages", server);
        Assert.Contains("RequireDirectConversationAsync", server);
        Assert.Contains("MinimumTypingSignalInterval", server);
    }

    private static TypingActivityEvent Activity(TypingConversationDto conversation, Guid accountId,
        string displayName, DateTimeOffset occurredAt, Guid? sessionId = null, bool isTyping = true) =>
        new(conversation, accountId, sessionId ?? Guid.NewGuid(), displayName, isTyping, occurredAt);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
