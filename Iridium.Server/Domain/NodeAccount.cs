using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class NodeAccount
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
    public string? Pronouns { get; set; }
    public string? Description { get; set; }
    public Guid? ActiveAvatarPresetId { get; set; }
    public long AvatarRevision { get; set; }
    public UserPresence PreferredPresence { get; set; } = UserPresence.Online;
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<CommunityMember> CommunityMemberships { get; set; } = [];
    public ICollection<Community> OwnedCommunities { get; set; } = [];
    public ICollection<ChannelMessage> Messages { get; set; } = [];
    public ICollection<AccountAvatarPreset> AvatarPresets { get; set; } = [];
}
