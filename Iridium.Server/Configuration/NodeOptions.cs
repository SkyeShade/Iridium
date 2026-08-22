using Iridium.Protocol;

namespace Iridium.Server.Configuration;

public sealed class NodeOptions
{
    public const string SectionName = "Node";

    public bool AllowRegistrations { get; set; } = true;

    public int MaxCommunitiesPerUser { get; set; } = 5;

    public int SessionIdleDays { get; set; } = 60;

    public int SessionAbsoluteDays { get; set; } = 365;

    public int SessionActivityUpdateMinutes { get; set; } = 15;

    public string? PublicAuthority { get; set; }

    public int MaxMessageCharacters { get; set; } = 10_000;

    public long MaxAttachmentBytes { get; set; } = NodeLimitDefaults.MaxAttachmentBytes;

    public int MaxAttachmentsPerMessage { get; set; } = 10;

    public string? AttachmentStoragePath { get; set; } = Path.Combine("data", "attachments");

    public long MaxAvatarBytes { get; set; } = ProfileAvatarLimits.MaximumUploadBytes;

    public int MaxAvatarDimension { get; set; } = ProfileAvatarLimits.MaximumSourceDimension;

    public long MaxAvatarPixels { get; set; } = ProfileAvatarLimits.MaximumDecodedPixels;

    public long MaxBannerBytes { get; set; } = ProfileBannerLimits.MaximumUploadBytes;

    public int MaxBannerDimension { get; set; } = ProfileBannerLimits.MaximumSourceDimension;

    public long MaxBannerPixels { get; set; } = ProfileBannerLimits.MaximumDecodedPixels;
}
