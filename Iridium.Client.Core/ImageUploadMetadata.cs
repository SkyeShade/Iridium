namespace Iridium.Client.Core;

public sealed record ImageUploadMetadata(string ContentType, string FileName);

public static class ImageUploadMetadataDetector
{
    public static ImageUploadMetadata Detect(ReadOnlySpan<byte> content, string fileName)
    {
        var (contentType, extension) = content switch
        {
            _ when content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8)
                => ("image/webp", ".webp"),
            _ when content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })
                => ("image/png", ".png"),
            _ when content.Length >= 3 && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff
                => ("image/jpeg", ".jpg"),
            _ when content.Length >= 6 && (content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8))
                => ("image/gif", ".gif"),
            _ => throw new InvalidOperationException("Choose a PNG, JPEG, WebP, or GIF image.")
        };
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "emoji";
        return new(contentType, baseName + extension);
    }
}
