namespace Iridium.Server.Domain;

public sealed class CommunityForumPost
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid ForumChannelId { get; set; }
    public Guid DiscussionChannelId { get; set; }
    public Guid RootMessageId { get; set; }
    public Guid AuthorAccountId { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int ReplyCount { get; set; }
    public bool IsLocked { get; set; }
    public bool IsPinned { get; set; }
    public required Community Community { get; set; }
    public required CommunityChannel ForumChannel { get; set; }
    public required CommunityChannel DiscussionChannel { get; set; }
    public required ChannelMessage RootMessage { get; set; }
    public required NodeAccount AuthorAccount { get; set; }
}
