using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class TypingActivityTiming
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(4);
    public static bool ShouldBroadcast(bool currentlyBroadcast, DateTimeOffset lastSignal, DateTimeOffset now) =>
        !currentlyBroadcast || now - lastSignal >= HeartbeatInterval;
}

public sealed class TypingIndicatorState
{
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(10);
    public const int MaximumDetailedTextLength = 48;
    private readonly Dictionary<TypingConversationDto, Dictionary<TypingActor, ActiveTyper>> _conversations = [];

    public bool Apply(TypingActivityEvent activity, Guid? localAccountId, DateTimeOffset receivedAt)
    {
        if (activity.AccountId == localAccountId) return false;
        if (!_conversations.TryGetValue(activity.Conversation, out var typers))
        {
            if (!activity.IsTyping) return false;
            _conversations[activity.Conversation] = typers = [];
        }
        var actor = new TypingActor(activity.AccountId, activity.SessionId);
        if (!activity.IsTyping && activity.SessionId == Guid.Empty)
        {
            var removed = false;
            foreach (var key in typers.Keys.Where(value => value.AccountId == activity.AccountId).ToArray())
                removed |= typers.Remove(key);
            if (typers.Count == 0) _conversations.Remove(activity.Conversation);
            return removed;
        }
        if (!activity.IsTyping)
        {
            var removed = typers.Remove(actor);
            if (typers.Count == 0) _conversations.Remove(activity.Conversation);
            return removed;
        }
        typers[actor] = new(activity.AccountId, activity.DisplayName, receivedAt);
        return true;
    }

    public bool Prune(DateTimeOffset now)
    {
        var changed = false;
        foreach (var (conversation, typers) in _conversations.ToArray())
        {
            foreach (var actor in typers.Where(value => now - value.Value.LastActivity >= InactivityTimeout)
                         .Select(value => value.Key).ToArray())
                changed |= typers.Remove(actor);
            if (typers.Count == 0) _conversations.Remove(conversation);
        }
        return changed;
    }

    public void Clear(TypingConversationDto conversation) => _conversations.Remove(conversation);
    public void Clear() => _conversations.Clear();

    public string? TextFor(TypingConversationDto? conversation)
    {
        if (conversation is null || !_conversations.TryGetValue(conversation, out var typers)) return null;
        var names = typers.Values.GroupBy(value => value.AccountId)
            .Select(group => group.OrderByDescending(value => value.LastActivity).First())
            .OrderByDescending(value => value.LastActivity)
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(value => value.DisplayName).ToArray();
        return Format(names);
    }

    public static string? Format(IReadOnlyList<string> displayNames)
    {
        if (displayNames.Count == 0) return null;
        if (displayNames.Count >= 3) return "Several people are typing...";
        if (displayNames.Count == 1) return $"{displayNames[0]} is typing...";
        var detailed = $"{displayNames[0]} and {displayNames[1]} are typing...";
        return detailed.Length <= MaximumDetailedTextLength ? detailed : "Several people are typing...";
    }

    private sealed record ActiveTyper(Guid AccountId, string DisplayName, DateTimeOffset LastActivity);
    private readonly record struct TypingActor(Guid AccountId, Guid SessionId);
}
