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
    public static bool HasActivePersona(Guid? profilePresetId) => profilePresetId.HasValue;

    public static OptimisticCommunityAuthorPresentation PresentationFor(CommunityMemberDto member,
        AccountAvatarPresetDto? selectedAccountPfp)
    {
        var chatDisplayName = member.ActiveChatDisplayName ?? member.DisplayName;
        if (member.ActiveChatAvatarPresetId is not null)
            return new(member.ProfilePresetId, chatDisplayName, member.ActiveChatAvatarPresetId,
                member.ActiveChatAvatarRevision);
        return selectedAccountPfp is { } pfp
            ? new(member.ProfilePresetId, chatDisplayName, pfp.Id, pfp.Revision)
            : new(member.ProfilePresetId, chatDisplayName, null, member.ActiveChatAvatarRevision);
    }

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
            // A server-loaded predecessor has no client-only assignment identity. Compare its
            // historical visual fields with the normalized transient visual fields that are known.
            var previousDisplayName = previousPresentation?.DisplayName ?? previous.Author.DisplayName;
            var currentDisplayName = currentPresentation?.DisplayName ?? current.Author.DisplayName;
            var previousAvatarRevision = previousPresentation?.AvatarRevision ?? previous.Author.AvatarRevision;
            var currentAvatarRevision = currentPresentation?.AvatarRevision ?? current.Author.AvatarRevision;
            if (!string.Equals(previousDisplayName, currentDisplayName, StringComparison.Ordinal))
                return true;
            if (previousAvatarRevision != currentAvatarRevision) return true;
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
