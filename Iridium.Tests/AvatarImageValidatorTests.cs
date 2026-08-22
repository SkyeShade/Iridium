using Iridium.Server.Configuration;
using Iridium.Server.Storage;
using Iridium.Protocol;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Iridium.Tests;

public sealed class AvatarImageValidatorTests
{
    // Small, decoded fixtures keep these tests focused on signature and decoder validation.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] Gif = Convert.FromHexString(
        "47494638396101000100800000000000FFFFFF" +
        "21F904000A0000002C0000000001000100000202440100" +
        "21F904000A0000002C00000000010001000002024401003B");

    [Fact]
    public async Task ValidPngAndGifAreAcceptedWithoutFlatteningTheirOriginalBytes()
    {
        var validator = Create();
        var png = await validator.ValidateAsync(new MemoryStream(Png), "image/png");
        var gif = await validator.ValidateAsync(new MemoryStream(Gif), "image/gif");

        Assert.Equal("image/png", png.ContentType);
        Assert.Equal(Png, png.Content);
        Assert.Equal("image/gif", gif.ContentType);
        Assert.Equal(Gif, gif.Content);
        Assert.True(gif.Animated);
        Assert.Equal(1, png.Width);
        Assert.Equal(1, gif.Height);
    }

    [Fact]
    public async Task DecoderAuthoritativelyAcceptsPngAliasJpegAndWebp()
    {
        var validator = Create();
        var pngAlias = await validator.ValidateAsync(new MemoryStream(Png), "image/x-png");
        var jpegBytes = Encode(SKEncodedImageFormat.Jpeg);
        var webpBytes = Encode(SKEncodedImageFormat.Webp);
        var jpeg = await validator.ValidateAsync(new MemoryStream(jpegBytes), "image/jpg");
        var webp = await validator.ValidateAsync(new MemoryStream(webpBytes), "image/webp");
        Assert.Equal("image/png", pngAlias.ContentType);
        Assert.Equal("image/jpeg", jpeg.ContentType);
        Assert.Equal("image/webp", webp.ContentType);
    }

    [Fact]
    public async Task MalformedAndExtensionSpoofedImagesAreRejected()
    {
        var validator = Create();
        await Assert.ThrowsAsync<AvatarImageValidationException>(() =>
            validator.ValidateAsync(new MemoryStream("not an image"u8.ToArray()), "image/png"));
        await Assert.ThrowsAsync<AvatarImageValidationException>(() =>
            validator.ValidateAsync(new MemoryStream(Gif), "image/png"));
    }

    [Fact]
    public async Task OversizedUploadIsRejectedBeforeDecode()
    {
        var validator = Create(maximumBytes: ProfileAvatarLimits.MaximumUploadBytes);
        var exception = await Assert.ThrowsAsync<AvatarImageValidationException>(() =>
            validator.ValidateAsync(new LengthOnlyStream(247_380_000), "image/png"));
        Assert.Equal(FileSizeDisplay.AvatarTooLarge(247_380_000), exception.Message);
    }

    [Fact]
    public async Task BannerUploadHasIndependentTwentyFiveMegabyteFriendlyLimit()
    {
        var validator = Create();
        var exception = await Assert.ThrowsAsync<AvatarImageValidationException>(() =>
            validator.ValidateBannerAsync(new LengthOnlyStream(31_420_000), "image/png"));
        Assert.Equal("This image is 31.42 MB. The maximum banner size is 25.00 MB.", exception.Message);
    }

    private static AvatarImageValidator Create(long maximumBytes = 1024 * 1024) =>
        new(Options.Create(new NodeOptions { MaxAvatarBytes = maximumBytes, MaxAvatarDimension = 4096 }));

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The validator should reject from stream metadata before reading.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
