using System.Globalization;

namespace Iridium.Protocol;

public static class ProfileAvatarLimits
{
    public const int MaximumPresets = 10;
    public const long MaximumUploadBytes = 200_000_000;
    public const long MaximumMultipartBytes = MaximumUploadBytes + 1_000_000;
    public const int MaximumSourceDimension = 8192;
    public const long MaximumDecodedPixels = 64_000_000;
}

public static class FileSizeDisplay
{
    public static string Megabytes(long bytes) =>
        (bytes / 1_000_000d).ToString("F2", CultureInfo.InvariantCulture) + " MB";

    public static string AvatarTooLarge(long actualBytes, long maximumBytes = ProfileAvatarLimits.MaximumUploadBytes) =>
        $"This image is {Megabytes(actualBytes)}. The maximum avatar size is {Megabytes(maximumBytes)}.";
}

public static class ProfileHubContract
{
    public const string Updated = "ProfileUpdated";
}

public sealed record ProfileUpdatedEvent(Guid AccountId, long AvatarRevision);

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
    DateTimeOffset UpdatedAt);

public sealed record AccountAvatarPresetsDto(
    Guid AccountId,
    Guid? ActiveAvatarPresetId,
    long AvatarRevision,
    IReadOnlyList<AccountAvatarPresetDto> Presets);

public sealed record UpdateAvatarCropRequest(double CropX, double CropY, double Zoom, bool SetActive = false);
