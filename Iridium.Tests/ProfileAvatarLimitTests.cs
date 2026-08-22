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
    public void AvatarLimitIsTwoHundredMegabytesAndTenPresets()
    {
        Assert.Equal(200_000_000, ProfileAvatarLimits.MaximumUploadBytes);
        Assert.Equal(10, ProfileAvatarLimits.MaximumPresets);
        Assert.Equal(
            "This image is 247.38 MB. The maximum avatar size is 200.00 MB.",
            FileSizeDisplay.AvatarTooLarge(247_380_000));
    }
}
