using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class ForumPostSubscription
{
    public Guid ForumPostId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public ForumPostNotificationLevel NotificationLevel { get; set; } = ForumPostNotificationLevel.MentionsOnly;
    public required CommunityForumPost ForumPost { get; set; }
    public required NodeAccount Account { get; set; }
}
