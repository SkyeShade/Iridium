using Iridium.Server.Configuration;
using Iridium.Protocol;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Iridium.Server.Storage;

public sealed record ValidatedAvatarImage(byte[] Content, string ContentType, int Width, int Height, bool Animated);

public interface IAvatarImageValidator
{
    Task<ValidatedAvatarImage> ValidateAsync(Stream source, string? declaredContentType,
        CancellationToken cancellationToken = default);
}

public sealed class AvatarImageValidationException(string message) : Exception(message);

public sealed class AvatarImageValidator(
    IOptions<NodeOptions> options,
    ILogger<AvatarImageValidator>? logger = null,
    IHostEnvironment? environment = null) : IAvatarImageValidator
{
    public async Task<ValidatedAvatarImage> ValidateAsync(Stream source, string? declaredContentType,
        CancellationToken cancellationToken = default)
    {
        var maximumBytes = options.Value.MaxAvatarBytes;
        if (source.CanSeek)
        {
            var remaining = source.Length - source.Position;
            if (remaining > maximumBytes)
                throw new AvatarImageValidationException(FileSizeDisplay.AvatarTooLarge(remaining, maximumBytes));
        }
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new AvatarImageValidationException(
                    FileSizeDisplay.AvatarTooLarge(buffer.Length + read, maximumBytes));
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var content = buffer.ToArray();
        if (content.Length == 0) throw new AvatarImageValidationException("The selected avatar is empty.");
        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new AvatarImageValidationException("The image could not be decoded.");
        var detectedType = ContentType(codec.EncodedFormat) ??
            throw new AvatarImageValidationException("Use a valid PNG, GIF, JPEG, or WebP image.");
        var normalizedDeclaredType = NormalizeContentType(declaredContentType);
        if (environment?.IsDevelopment() == true)
            logger?.LogInformation("AVATAR UPLOAD Detected={DetectedType} Declared={DeclaredType} Bytes={Size} Width={Width} Height={Height}",
                detectedType, normalizedDeclaredType ?? "(none)", content.LongLength, codec.Info.Width, codec.Info.Height);
        if (normalizedDeclaredType is not null && normalizedDeclaredType != detectedType)
        {
            var detail = environment?.IsDevelopment() == true
                ? $" (Declared={normalizedDeclaredType}, Detected={detectedType})" : string.Empty;
            throw new AvatarImageValidationException($"The file contents do not match its declared image type.{detail}");
        }
        var maximumDimension = options.Value.MaxAvatarDimension;
        if (codec.Info.Width > maximumDimension || codec.Info.Height > maximumDimension)
            throw new AvatarImageValidationException($"Avatar dimensions may not exceed {maximumDimension} pixels.");
        if ((long)codec.Info.Width * codec.Info.Height > options.Value.MaxAvatarPixels)
            throw new AvatarImageValidationException(
                $"The decoded avatar exceeds the {options.Value.MaxAvatarPixels:N0}-pixel safety limit.");
        return new(content, detectedType, codec.Info.Width, codec.Info.Height,
            detectedType == "image/gif" && codec.FrameCount > 1);
    }

    private static string? ContentType(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => "image/png",
        SKEncodedImageFormat.Gif => "image/gif",
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        SKEncodedImageFormat.Webp => "image/webp",
        _ => null
    };

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)) return null;
        var value = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return value switch
        {
            "image/x-png" => "image/png",
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            _ => value
        };
    }
}
