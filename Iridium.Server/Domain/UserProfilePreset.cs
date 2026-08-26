namespace Iridium.Server.Domain;

public sealed class UserProfilePreset
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required NodeAccount Account { get; set; }
    public Guid CommunityId { get; set; }
    public required Community Community { get; set; }
    public required string DisplayName { get; set; }
    public Guid? AvatarPresetId { get; set; }
    public AccountAvatarPreset? AvatarPreset { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
