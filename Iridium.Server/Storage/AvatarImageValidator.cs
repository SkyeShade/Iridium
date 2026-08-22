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
    Task<ValidatedAvatarImage> ValidateBannerAsync(Stream source, string? declaredContentType,
        CancellationToken cancellationToken = default);
}

public sealed class AvatarImageValidationException(string message) : Exception(message);

public sealed class AvatarImageValidator(
    IOptions<NodeOptions> options,
    ILogger<AvatarImageValidator>? logger = null,
    IHostEnvironment? environment = null) : IAvatarImageValidator
{
    public async Task<ValidatedAvatarImage> ValidateAsync(Stream source, string? declaredContentType,
        CancellationToken cancellationToken = default) =>
        await ValidateCoreAsync(source, declaredContentType, false, cancellationToken);

    public async Task<ValidatedAvatarImage> ValidateBannerAsync(Stream source, string? declaredContentType,
        CancellationToken cancellationToken = default) =>
        await ValidateCoreAsync(source, declaredContentType, true, cancellationToken);

    private async Task<ValidatedAvatarImage> ValidateCoreAsync(Stream source, string? declaredContentType,
        bool banner, CancellationToken cancellationToken)
    {
        var maximumBytes = banner ? options.Value.MaxBannerBytes : options.Value.MaxAvatarBytes;
        string TooLarge(long value) => banner
            ? FileSizeDisplay.BannerTooLarge(value, maximumBytes)
            : FileSizeDisplay.AvatarTooLarge(value, maximumBytes);
        if (source.CanSeek)
        {
            var remaining = source.Length - source.Position;
            if (remaining > maximumBytes)
                throw new AvatarImageValidationException(TooLarge(remaining));
        }
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new AvatarImageValidationException(
                    TooLarge(buffer.Length + read));
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var content = buffer.ToArray();
        if (content.Length == 0)
            throw new AvatarImageValidationException($"The selected {(banner ? "banner" : "avatar")} is empty.");
        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new AvatarImageValidationException("The image could not be decoded.");
        var detectedType = ContentType(codec.EncodedFormat) ??
            throw new AvatarImageValidationException("Use a valid PNG, GIF, JPEG, or WebP image.");
        var normalizedDeclaredType = NormalizeContentType(declaredContentType);
        if (environment?.IsDevelopment() == true)
            logger?.LogInformation("PROFILE MEDIA UPLOAD Kind={Kind} Detected={DetectedType} Declared={DeclaredType} Bytes={Size} Width={Width} Height={Height}",
                banner ? "Banner" : "Avatar", detectedType, normalizedDeclaredType ?? "(none)", content.LongLength,
                codec.Info.Width, codec.Info.Height);
        if (normalizedDeclaredType is not null && normalizedDeclaredType != detectedType)
        {
            var detail = environment?.IsDevelopment() == true
                ? $" (Declared={normalizedDeclaredType}, Detected={detectedType})" : string.Empty;
            throw new AvatarImageValidationException($"The file contents do not match its declared image type.{detail}");
        }
        var maximumDimension = banner ? options.Value.MaxBannerDimension : options.Value.MaxAvatarDimension;
        if (codec.Info.Width > maximumDimension || codec.Info.Height > maximumDimension)
            throw new AvatarImageValidationException(
                $"{(banner ? "Banner" : "Avatar")} dimensions may not exceed {maximumDimension} pixels.");
        var maximumPixels = banner ? options.Value.MaxBannerPixels : options.Value.MaxAvatarPixels;
        if ((long)codec.Info.Width * codec.Info.Height > maximumPixels)
            throw new AvatarImageValidationException(
                $"The decoded {(banner ? "banner" : "avatar")} exceeds the {maximumPixels:N0}-pixel safety limit.");
        return new(content, detectedType, codec.Info.Width, codec.Info.Height,
            detectedType is "image/gif" or "image/webp" && codec.FrameCount > 1);
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
