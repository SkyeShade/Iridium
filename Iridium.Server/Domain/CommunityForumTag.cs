using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class CommunityForumTag
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid ChannelId { get; set; }
    public required string Name { get; set; }
    public ReactionEmojiKind? EmojiKind { get; set; }
    public string? StandardEmoji { get; set; }
    public Guid? CustomEmojiId { get; set; }
    public bool Moderated { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required CommunityChannel Channel { get; set; }
    public CommunityEmoji? CustomEmoji { get; set; }
    public ICollection<CommunityForumPostTag> PostAssignments { get; set; } = [];
}

public sealed class CommunityForumPostTag
{
    public Guid PostId { get; set; }
    public Guid TagId { get; set; }
    public required CommunityForumPost Post { get; set; }
    public required CommunityForumTag Tag { get; set; }
}
