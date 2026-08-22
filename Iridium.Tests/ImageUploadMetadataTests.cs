using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class ImageUploadMetadataTests
{
    [Fact]
    public void WebpBytesOverrideIncorrectPngMultipartMetadata()
    {
        var bytes = "RIFF0000WEBP"u8.ToArray();
        var metadata = ImageUploadMetadataDetector.Detect(bytes, "emoji.png");
        Assert.Equal("image/webp", metadata.ContentType);
        Assert.Equal("emoji.webp", metadata.FileName);
    }

    [Theory]
    [InlineData("png", "image/png", ".png")]
    [InlineData("jpeg", "image/jpeg", ".jpg")]
    [InlineData("gif", "image/gif", ".gif")]
    public void SignatureDeterminesDeclaredUploadType(string kind, string expectedType, string expectedExtension)
    {
        var bytes = kind switch
        {
            "png" => new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a },
            "jpeg" => new byte[] { 0xff, 0xd8, 0xff },
            _ => "GIF89a"u8.ToArray()
        };
        var metadata = ImageUploadMetadataDetector.Detect(bytes, "wrong.bin");
        Assert.Equal(expectedType, metadata.ContentType);
        Assert.EndsWith(expectedExtension, metadata.FileName);
    }
}
