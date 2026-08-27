using System.Globalization;

namespace Iridium.Protocol;

public static class ProfileAvatarLimits
{
    public const int MaximumPresets = 10;
    public const long MaximumUploadBytes = 10_000_000;
    public const long MaximumMultipartBytes = MaximumUploadBytes + 1_000_000;
    public const int MaximumSourceDimension = 8192;
    public const long MaximumDecodedPixels = 64_000_000;
}

public static class ProfileBannerLimits
{
    public const int MaximumPresets = 4;
    public const long MaximumUploadBytes = 25_000_000;
    public const long MaximumMultipartBytes = MaximumUploadBytes + 1_000_000;
    public const int MaximumSourceDimension = 8192;
    public const long MaximumDecodedPixels = 64_000_000;
    public const int CropWidth = 1000;
    public const int CropHeight = 400;
    public const int ProcessedWidth = 1200;
    public const int ProcessedHeight = 480;
    public const double AspectRatio = CropWidth / (double)CropHeight;
}

public static class CommunityMediaLimits
{
    public const int MaximumAvatarPresets = ProfileAvatarLimits.MaximumPresets;
    public const int MaximumBannerPresets = ProfileBannerLimits.MaximumPresets;
}

/// <summary>The canonical 16:9 geometry used by every Community banner surface.</summary>
public static class CommunityBannerLimits
{
    public const int CropWidth = 960;
    public const int CropHeight = 540;
    public const int ProcessedWidth = 1200;
    public const int ProcessedHeight = 675;
    public const double AspectRatio = CropWidth / (double)CropHeight;
}

public static class FileSizeDisplay
{
    public static string Megabytes(long bytes) =>
        (bytes / 1_000_000d).ToString("F2", CultureInfo.InvariantCulture) + " MB";

    public static string AvatarTooLarge(long actualBytes, long maximumBytes = ProfileAvatarLimits.MaximumUploadBytes) =>
        $"This image is {Megabytes(actualBytes)}. The maximum avatar size is {Megabytes(maximumBytes)}.";

    public static string BannerTooLarge(long actualBytes, long maximumBytes = ProfileBannerLimits.MaximumUploadBytes) =>
        $"This image is {Megabytes(actualBytes)}. The maximum banner size is {Megabytes(maximumBytes)}.";
}

public static class ProfileHubContract
{
    public const string Updated = "ProfileUpdated";
}

public sealed record ProfileUpdatedEvent(
    Guid AccountId,
    long AvatarRevision,
    long BannerRevision,
    string DisplayName,
    string? Pronouns,
    string? Description);

public sealed record ProfileAvatarDto(bool HasAvatar, string? AvatarUrl, long Revision,
    double CropX = 0, double CropY = 0, double Zoom = 1, int Width = 0, int Height = 0);

public sealed record AccountAvatarPresetDto(
    Guid Id,
    int SlotIndex,
    string AvatarUrl,
    long Revision,
    string ContentType,
    int Width,
    int Height,
    double CropX,
    double CropY,
    double Zoom,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DisplayName = null);

public sealed record AccountAvatarPresetsDto(
    Guid AccountId,
    Guid? ActiveAvatarPresetId,
    long AvatarRevision,
    IReadOnlyList<AccountAvatarPresetDto> Presets);

public sealed record UpdateAvatarCropRequest(double CropX, double CropY, double Zoom, bool SetActive = false);
public sealed record UpdateProfilePresetRequest(string DisplayName);

public sealed record UserProfilePresetDto(
    Guid Id,
    Guid AccountId,
    Guid CommunityId,
    string DisplayName,
    AccountAvatarPresetDto? Avatar,
    int Position,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateUserProfilePresetRequest(string DisplayName);
public sealed record SetUserProfilePresetAvatarRequest(Guid? AvatarPresetId);

public sealed record ProfileBannerDto(bool HasBanner, string? BannerUrl, string? SourceUrl, long Revision,
    double CropX = 0, double CropY = 0, double Zoom = 1, int Width = 0, int Height = 0,
    bool IsProcessed = false);

public sealed record AccountBannerPresetDto(
    Guid Id,
    int SlotIndex,
    string BannerUrl,
    string SourceUrl,
    long Revision,
    string ContentType,
    int Width,
    int Height,
    double CropX,
    double CropY,
    double Zoom,
    bool IsProcessed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AccountBannerPresetsDto(
    Guid AccountId,
    Guid? ActiveBannerPresetId,
    long BannerRevision,
    IReadOnlyList<AccountBannerPresetDto> Presets);

public sealed record UpdateBannerCropRequest(double CropX, double CropY, double Zoom, bool SetActive = false);
