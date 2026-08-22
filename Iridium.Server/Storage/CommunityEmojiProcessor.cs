using Iridium.Protocol;
using SkiaSharp;

namespace Iridium.Server.Storage;

public static class CommunityEmojiProcessor
{
    public static ValidatedAvatarImage Process(ValidatedAvatarImage image)
    {
        if (image.Animated) return image;
        using var source = SKBitmap.Decode(image.Content) ??
            throw new AvatarImageValidationException("The emoji could not be decoded.");
        var maximum = CommunityEmojiLimits.ProcessedDimension;
        var scale = Math.Min(1d, maximum / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var rendered = SKImage.FromBitmap(output);
        using var encoded = rendered.Encode(SKEncodedImageFormat.Webp, 90);
        return new(encoded.ToArray(), "image/webp", width, height, false);
    }
}
