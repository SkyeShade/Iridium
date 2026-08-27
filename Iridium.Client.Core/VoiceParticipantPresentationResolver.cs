using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record VoiceParticipantPresentation(
    Guid AccountId,
    string DisplayName,
    PublicPresence Presence,
    long AvatarRevision);

public static class VoiceParticipantPresentationResolver
{
    public static VoiceParticipantPresentation ResolveBase(
        Guid accountId,
        string fallbackDisplayName,
        PublicPresence fallbackPresence,
        long fallbackAvatarRevision,
        NodeAccountDto? localAccount,
        PublicPresence localPresence,
        IEnumerable<CommunityMemberDto> communityMembers,
        IEnumerable<DirectConversationDto> directConversations,
        IEnumerable<FriendDto> friends)
    {
        var member = communityMembers.FirstOrDefault(value => value.AccountId == accountId);
        if (member is not null)
            return new(accountId, member.DisplayName, member.Presence, member.AvatarRevision);

        var direct = directConversations.Select(value => value.OtherParticipant)
            .FirstOrDefault(value => value.AccountId == accountId);
        if (direct is not null)
            return new(accountId, direct.DisplayName, direct.Presence, fallbackAvatarRevision);

        var friend = friends.FirstOrDefault(value => value.AccountId == accountId);
        if (friend is not null)
            return new(accountId, friend.DisplayName, friend.Presence, fallbackAvatarRevision);

        if (localAccount?.Id == accountId)
            return new(accountId, localAccount.DisplayName, localPresence, localAccount.AvatarRevision);

        return new(accountId, fallbackDisplayName, fallbackPresence, fallbackAvatarRevision);
    }
}
