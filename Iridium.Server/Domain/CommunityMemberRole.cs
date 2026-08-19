namespace Iridium.Server.Domain;

public sealed class CommunityMemberRole
{
    public Guid CommunityId { get; set; }
    public Guid AccountId { get; set; }
    public Guid RoleId { get; set; }
    public required CommunityMember Member { get; set; }
    public required CommunityRole Role { get; set; }
}
