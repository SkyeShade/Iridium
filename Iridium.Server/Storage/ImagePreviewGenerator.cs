using SkiaSharp;

namespace Iridium.Server.Storage;

public sealed record GeneratedImagePreview(
    byte[] Content,
    string ContentType,
    int OriginalWidth,
    int OriginalHeight,
    string AverageColor);

public interface IImagePreviewGenerator
{
    Task<GeneratedImagePreview?> GenerateAsync(Stream source, string declaredContentType,
        CancellationToken cancellationToken = default);
}

public sealed class ImagePreviewGenerator : IImagePreviewGenerator
{
    private const int MaximumPreviewDimension = 1280;
    private const long MaximumDecodedPixels = 100_000_000;
    private static readonly HashSet<string> CandidateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "image/tiff"
    };

    public async Task<GeneratedImagePreview?> GenerateAsync(Stream source, string declaredContentType,
        CancellationToken cancellationToken = default)
    {
        if (!CandidateTypes.Contains(declaredContentType) || !source.CanSeek) return null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var codec = SKCodec.Create(source);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > MaximumDecodedPixels) return null;
            var originalWidth = codec.Info.Width;
            var originalHeight = codec.Info.Height;
            source.Position = 0;
            using var decoded = SKBitmap.Decode(source);
            if (decoded is null) return null;
            var scale = Math.Min(1d, Math.Min(
                MaximumPreviewDimension / (double)decoded.Width,
                MaximumPreviewDimension / (double)decoded.Height));
            var targetWidth = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            using var resized = targetWidth == decoded.Width && targetHeight == decoded.Height
                ? decoded.Copy()
                : decoded.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resized is null) return null;

            var (hasTransparentPixels, averageColor) = AnalyzePixels(resized);
            var preserveTransparency = hasTransparentPixels;
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(
                preserveTransparency ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Webp,
                preserveTransparency ? 100 : 82);
            if (encoded is null) return null;
            return new(encoded.ToArray(), preserveTransparency ? "image/png" : "image/webp",
                originalWidth, originalHeight, averageColor);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (bool HasTransparency, string AverageColor) AnalyzePixels(SKBitmap image)
    {
        var transparent = false;
        long red = 0, green = 0, blue = 0, weight = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                transparent |= pixel.Alpha < byte.MaxValue;
                if (pixel.Alpha == 0) continue;
                red += pixel.Red * pixel.Alpha;
                green += pixel.Green * pixel.Alpha;
                blue += pixel.Blue * pixel.Alpha;
                weight += pixel.Alpha;
            }
        }
        return weight == 0
            ? (transparent, "#252936")
            : (transparent, $"#{red / weight:X2}{green / weight:X2}{blue / weight:X2}");
    }
}
