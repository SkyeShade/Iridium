using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class CommunityThread
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid ParentChannelId { get; set; }
    public Guid DiscussionChannelId { get; set; }
    public Guid OwnerAccountId { get; set; }
    public Guid? CreatedFromMessageId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public bool IsArchived { get; set; }
    public bool IsLocked { get; set; }
    public bool IsPrivate { get; set; }
    public ThreadAutoArchiveDuration AutoArchiveDuration { get; set; } = ThreadAutoArchiveDuration.ThreeDays;
    public int SlowmodeSeconds { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int ReplyCount { get; set; }
    public required Community Community { get; set; }
    public required CommunityChannel ParentChannel { get; set; }
    public required CommunityChannel DiscussionChannel { get; set; }
    public required NodeAccount OwnerAccount { get; set; }
    public ChannelMessage? CreatedFromMessage { get; set; }
    public ICollection<CommunityThreadMember> Members { get; set; } = [];
}

public sealed class CommunityThreadMember
{
    public Guid ThreadId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public ThreadNotificationLevel NotificationLevel { get; set; } = ThreadNotificationLevel.MentionsOnly;
    public required CommunityThread Thread { get; set; }
    public required NodeAccount Account { get; set; }
}
