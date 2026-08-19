using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class CommunityRole
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    public CommunityPermission Permissions { get; set; }
    public bool IsDefault { get; set; }
    public string? Color { get; set; }
    public bool DisplaySeparately { get; set; }
    public bool IsMentionable { get; set; }
    public required Community Community { get; set; }
    public ICollection<CommunityMemberRole> Members { get; set; } = [];
}
