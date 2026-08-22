using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class ProfileAvatarLimitTests
{
    [Theory]
    [InlineData(16_991_730, "16.99 MB")]
    [InlineData(200_000_000, "200.00 MB")]
    [InlineData(247_380_000, "247.38 MB")]
    public void FileSizesUseFriendlyDecimalMegabytes(long bytes, string expected) =>
        Assert.Equal(expected, FileSizeDisplay.Megabytes(bytes));

    [Fact]
    public void AvatarUploadLimitIsCentralizedAndPresetLimitIsTen()
    {
        Assert.True(ProfileAvatarLimits.MaximumUploadBytes > 0);
        Assert.Equal(10, ProfileAvatarLimits.MaximumPresets);
        Assert.Equal(
            $"This image is 247.38 MB. The maximum avatar size is {FileSizeDisplay.Megabytes(ProfileAvatarLimits.MaximumUploadBytes)}.",
            FileSizeDisplay.AvatarTooLarge(247_380_000));
    }

    [Fact]
    public void BannerUploadLimitIsSeparateAndPresetLimitIsFour()
    {
        Assert.Equal(25_000_000, ProfileBannerLimits.MaximumUploadBytes);
        Assert.Equal(4, ProfileBannerLimits.MaximumPresets);
        Assert.Equal(10, ProfileAvatarLimits.MaximumPresets);
        Assert.Equal(
            "This image is 31.42 MB. The maximum banner size is 25.00 MB.",
            FileSizeDisplay.BannerTooLarge(31_420_000));
    }
}
