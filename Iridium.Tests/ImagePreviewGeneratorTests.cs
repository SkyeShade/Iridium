using Iridium.Server.Storage;
using SkiaSharp;

namespace Iridium.Tests;

public sealed class ImagePreviewGeneratorTests
{
    [Theory]
    [InlineData(false, "image/webp")]
    [InlineData(true, "image/png")]
    public async Task PreviewIsBoundedAndUsesTransparencyAwareFormat(bool transparent, string expectedType)
    {
        var source = CreatePng(1600, 800, transparent);
        var preview = await new ImagePreviewGenerator().GenerateAsync(new MemoryStream(source), "image/png");

        Assert.NotNull(preview);
        Assert.Equal(1600, preview.OriginalWidth);
        Assert.Equal(800, preview.OriginalHeight);
        Assert.Equal(expectedType, preview.ContentType);
        Assert.Matches("^#[0-9A-F]{6}$", preview.AverageColor);
        using var decoded = SKBitmap.Decode(preview.Content);
        Assert.NotNull(decoded);
        Assert.Equal(1280, decoded.Width);
        Assert.Equal(640, decoded.Height);
        Assert.Equal(transparent, decoded.Pixels.Any(color => color.Alpha < byte.MaxValue));
    }

    private static byte[] CreatePng(int width, int height, bool transparent)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(30, 100, 180));
        if (transparent)
        {
            for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                bitmap.SetPixel(x, y, SKColors.Transparent);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
