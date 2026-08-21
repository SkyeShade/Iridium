namespace Iridium.Server.Domain;

public sealed class CommunityCategory
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    public Guid? ParentCategoryId { get; set; }
    public required Community Community { get; set; }
    public CommunityCategory? ParentCategory { get; set; }
    public ICollection<CommunityCategory> ChildCategories { get; set; } = [];
    public ICollection<CommunityChannel> Channels { get; set; } = [];
}
