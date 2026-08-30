using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class CommunityChannel
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? ParentForumChannelId { get; set; }
    public required string Name { get; set; }
    public CommunityChannelKind Kind { get; set; } = CommunityChannelKind.Text;
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool PermissionsSyncedToCategory { get; set; }
    public bool RequireTag { get; set; }
    public bool AllowDocumentEmbeds { get; set; }
    public CommunityChannelEmbedProvider? EmbedProvider { get; set; }
    public string? EmbedUrl { get; set; }
    public required Community Community { get; set; }
    public CommunityCategory? Category { get; set; }
    public ICollection<ChannelMessage> Messages { get; set; } = [];
    public ICollection<CommunityChannelReadState> ReadStates { get; set; } = [];
}
