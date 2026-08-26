namespace Iridium.Server.Domain;

public sealed class CommunityMember
{
    public Guid CommunityId { get; set; }
    public Guid AccountId { get; set; }
    public string? Nickname { get; set; }
    public Guid? ProfilePresetId { get; set; }
    public UserProfilePreset? ProfilePreset { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public required Community Community { get; set; }
    public required NodeAccount Account { get; set; }
    public ICollection<CommunityMemberRole> Roles { get; set; } = [];
}
