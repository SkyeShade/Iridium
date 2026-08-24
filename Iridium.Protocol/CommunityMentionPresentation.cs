namespace Iridium.Protocol;

public static class CommunityMentionPresentation
{
    public static bool IsTargetedAt(ChannelMessageDto message, Guid accountId,
        CommunityManagementDto? management = null)
    {
        if (message.Mentions is null) return false;
        var member = management?.Members.FirstOrDefault(value => value.AccountId == accountId);
        return message.Mentions.Any(mention => mention.Kind switch
        {
            CommunityMentionKind.Account => mention.TargetId == accountId,
            CommunityMentionKind.Role => mention.TargetId is { } roleId && member?.RoleIds.Contains(roleId) == true,
            CommunityMentionKind.Everyone => true,
            _ => false
        });
    }

    public static bool ShouldNotify(ChannelMessageDto message, Guid accountId,
        CommunityManagementDto? management = null) =>
        message.Author.AccountId != accountId && IsTargetedAt(message, accountId, management);

    public static bool ShouldDeliverNotification(Guid authorAccountId, Guid targetedAccountId) =>
        authorAccountId != targetedAccountId;

}
