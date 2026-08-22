using Iridium.Protocol;
using SkiaSharp;

namespace Iridium.Server.Storage;

public sealed record ProcessedBannerImage(byte[] Content, string ContentType = "image/webp");

public static class BannerImageProcessor
{
    public static ProcessedBannerImage? Process(ValidatedAvatarImage image, double cropX, double cropY, double zoom)
    {
        if (image.Animated) return null;
        using var source = SKBitmap.Decode(image.Content);
        if (source is null) throw new AvatarImageValidationException("The banner could not be decoded for processing.");
        var sourceRect = SourceCrop(source.Width, source.Height, cropX, cropY, zoom);
        using var output = new SKBitmap(ProfileBannerLimits.ProcessedWidth, ProfileBannerLimits.ProcessedHeight,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, sourceRect,
                new SKRect(0, 0, ProfileBannerLimits.ProcessedWidth, ProfileBannerLimits.ProcessedHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        using var rendered = SKImage.FromBitmap(output);
        using var encoded = rendered.Encode(SKEncodedImageFormat.Webp, 90);
        return new(encoded.ToArray());
    }

    public static SKRect SourceCrop(int sourceWidth, int sourceHeight, double normalizedX, double normalizedY,
        double zoom)
    {
        zoom = Math.Clamp(zoom, 1, 3);
        var minimumScale = Math.Max(ProfileBannerLimits.CropWidth / (double)sourceWidth,
            ProfileBannerLimits.CropHeight / (double)sourceHeight);
        var scale = minimumScale * zoom;
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var maximumOffsetX = Math.Max(0, (renderedWidth - ProfileBannerLimits.CropWidth) / 2);
        var maximumOffsetY = Math.Max(0, (renderedHeight - ProfileBannerLimits.CropHeight) / 2);
        var offsetX = Math.Clamp(normalizedX, -1, 1) * maximumOffsetX;
        var offsetY = Math.Clamp(normalizedY, -1, 1) * maximumOffsetY;
        var cropWidth = ProfileBannerLimits.CropWidth / scale;
        var cropHeight = ProfileBannerLimits.CropHeight / scale;
        var centerX = sourceWidth / 2d - offsetX / scale;
        var centerY = sourceHeight / 2d - offsetY / scale;
        return new SKRect((float)(centerX - cropWidth / 2), (float)(centerY - cropHeight / 2),
            (float)(centerX + cropWidth / 2), (float)(centerY + cropHeight / 2));
    }
}
