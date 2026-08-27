using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.UI;

public sealed record OptimisticCommunityAuthorPresentation(
    Guid? ProfilePresetId,
    string DisplayName,
    Guid? AvatarPresetId,
    long AvatarRevision);

/// <summary>
/// Supplies a transient grouping decision for Community messages that have not yet received their
/// immutable server-side author snapshot. Confirmed messages always use <see cref="MessageGrouping"/>.
/// </summary>
public static class OptimisticMessageGrouping
{
    public static bool StartsNewGroup(ChannelMessageDto? previous, ChannelMessageDto current,
        IReadOnlyDictionary<Guid, OptimisticCommunityAuthorPresentation>? localPresentations = null)
    {
        if (current.CommunityId == Guid.Empty || previous is null ||
            (previous.DeliveryState != MessageDeliveryState.Pending &&
             current.DeliveryState != MessageDeliveryState.Pending))
            return MessageGrouping.StartsNewGroup(previous, current);

        if (previous.Kind != MessageKind.User || current.Kind != MessageKind.User) return true;
        if (previous.Author.AccountId != current.Author.AccountId) return true;

        var previousPresentation = LocalPresentation(previous, localPresentations);
        var currentPresentation = LocalPresentation(current, localPresentations);
        if (previousPresentation is not null && currentPresentation is not null)
        {
            if (previousPresentation != currentPresentation) return true;
        }
        else
        {
            // A server-loaded predecessor has no client-only assignment identity. Retain the safe
            // visual comparison used before local send-time presentation tracking is available.
            if (!string.Equals(previous.Author.DisplayName, current.Author.DisplayName, StringComparison.Ordinal))
                return true;
            if (previous.Author.AvatarRevision != current.Author.AvatarRevision) return true;
        }

        if (previous.IsDeleted || current.IsDeleted) return true;
        if (current.ReplyTo is not null) return true;
        return current.CreatedAt - previous.CreatedAt > TimeSpan.FromMinutes(1);
    }

    private static OptimisticCommunityAuthorPresentation? LocalPresentation(ChannelMessageDto message,
        IReadOnlyDictionary<Guid, OptimisticCommunityAuthorPresentation>? presentations) =>
        message.ClientMessageId is { } clientMessageId && presentations?.TryGetValue(clientMessageId, out var value) == true
            ? value
            : null;
}
