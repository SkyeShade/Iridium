using System.Text;
using System.Buffers.Binary;

namespace Iridium.Server.Storage;

public sealed class AttachmentMediaValidationException(string message) : Exception(message);

public interface IAttachmentMediaTypeValidator
{
    Task<string> ValidateAsync(Stream source, string? declaredContentType, CancellationToken cancellationToken = default);
}

public sealed class AttachmentMediaTypeValidator : IAttachmentMediaTypeValidator
{
    private static readonly HashSet<string> Mp4Brands = new(StringComparer.Ordinal)
    {
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "avc1", "dash",
        "mp41", "mp42", "mp71", "M4V ", "M4A ", "MSNV", "F4V "
    };

    public async Task<string> ValidateAsync(Stream source, string? declaredContentType,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(declaredContentType)
            ? "application/octet-stream"
            : declaredContentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var count = await source.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        var boxSize = read == header.Length ? BinaryPrimitives.ReadUInt32BigEndian(header) : 0;
        var mp4 = read == header.Length && boxSize >= 16 && (!source.CanSeek || boxSize <= source.Length) &&
                  header[4] == (byte)'f' && header[5] == (byte)'t' &&
                  header[6] == (byte)'y' && header[7] == (byte)'p' &&
                  Mp4Brands.Contains(Encoding.ASCII.GetString(header, 8, 4));
        if (normalized == "video/mp4" && !mp4)
            throw new AttachmentMediaValidationException("The selected MP4 file does not have a valid MP4 signature.");
        return mp4 ? "video/mp4" : normalized;
    }
}
