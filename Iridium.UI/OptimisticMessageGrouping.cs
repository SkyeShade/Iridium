using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.UI;

/// <summary>
/// Supplies a transient grouping decision for Community messages that have not yet received their
/// immutable server-side author snapshot. Confirmed messages always use <see cref="MessageGrouping"/>.
/// </summary>
public static class OptimisticMessageGrouping
{
    public static bool StartsNewGroup(ChannelMessageDto? previous, ChannelMessageDto current)
    {
        if (current.DeliveryState != MessageDeliveryState.Pending || current.CommunityId == Guid.Empty)
            return MessageGrouping.StartsNewGroup(previous, current);

        if (previous is null) return true;
        if (previous.Kind != MessageKind.User || current.Kind != MessageKind.User) return true;
        if (previous.Author.AccountId != current.Author.AccountId) return true;

        // The pending author is the currently resolved Community presentation. Compare that visual
        // presentation without pretending it is already an immutable historical snapshot.
        if (!string.Equals(previous.Author.DisplayName, current.Author.DisplayName, StringComparison.Ordinal))
            return true;
        if (previous.Author.AvatarRevision != current.Author.AvatarRevision) return true;

        if (previous.IsDeleted || current.IsDeleted) return true;
        if (current.ReplyTo is not null) return true;
        return current.CreatedAt - previous.CreatedAt > TimeSpan.FromMinutes(1);
    }
}
