namespace Iridium.Server.Domain;

public sealed class Community
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid OwnerAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ActiveAvatarPresetId { get; set; }
    public long AvatarRevision { get; set; }
    public Guid? ActiveBannerPresetId { get; set; }
    public long BannerRevision { get; set; }
    public required NodeAccount OwnerAccount { get; set; }
    public ICollection<CommunityMember> Members { get; set; } = [];
    public ICollection<CommunityRole> Roles { get; set; } = [];
    public ICollection<CommunityInvite> Invites { get; set; } = [];
    public ICollection<CommunityBan> Bans { get; set; } = [];
    public ICollection<CommunityCategory> Categories { get; set; } = [];
    public ICollection<CommunityChannel> Channels { get; set; } = [];
    public ICollection<CommunityMediaPreset> MediaPresets { get; set; } = [];
    public ICollection<CommunityEmoji> Emojis { get; set; } = [];
    public ICollection<UserProfilePreset> ProfilePresets { get; set; } = [];
}
