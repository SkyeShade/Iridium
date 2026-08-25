namespace Iridium.Client.Core;

public static class CommunityForumPostDrafts
{
    public static MessageDraftScope TitleScope(string nodeAuthority, Guid accountId, Guid communityId,
        Guid forumChannelId) => new(nodeAuthority, accountId, $"forum-new-title-{communityId:N}", forumChannelId);

    public static MessageDraftScope BodyScope(string nodeAuthority, Guid accountId, Guid communityId,
        Guid forumChannelId) => new(nodeAuthority, accountId, $"forum-new-body-{communityId:N}", forumChannelId);
}
