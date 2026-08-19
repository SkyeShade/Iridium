using System.Buffers.Binary;
using Microsoft.AspNetCore.WebUtilities;

namespace Iridium.Server.Api;

internal readonly record struct MessageHistoryCursor(long UtcTicks, Guid MessageId)
{
    public static string Encode(DateTimeOffset createdAt, Guid messageId)
    {
        Span<byte> bytes = stackalloc byte[24];
        BinaryPrimitives.WriteInt64BigEndian(bytes, createdAt.UtcTicks);
        messageId.TryWriteBytes(bytes[8..]);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    public static bool TryDecode(string? value, out MessageHistoryCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value);
            if (bytes.Length != 24) return false;
            cursor = new(BinaryPrimitives.ReadInt64BigEndian(bytes), new Guid(bytes.AsSpan(8, 16)));
            return true;
        }
        catch (FormatException) { return false; }
    }
}
