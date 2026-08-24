using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Iridium.Server.Security;

public interface IAttachmentPlaybackTokenService
{
    (string Token, DateTimeOffset ExpiresAt) Issue(Guid attachmentId, Guid accountId);
    bool TryValidate(Guid attachmentId, string? token, out Guid accountId);
}

public sealed class AttachmentPlaybackTokenService(TimeProvider timeProvider) : IAttachmentPlaybackTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid attachmentId, Guid accountId)
    {
        var expiresAt = timeProvider.GetUtcNow().Add(Lifetime);
        var payload = $"{attachmentId:N}.{accountId:N}.{expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        var signature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return ($"{Base64Url(Encoding.UTF8.GetBytes(payload))}.{Base64Url(signature)}", expiresAt);
    }

    public bool TryValidate(Guid attachmentId, string? token, out Guid accountId)
    {
        accountId = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var separator = token.LastIndexOf('.');
        if (separator <= 0) return false;
        try
        {
            var payloadBytes = FromBase64Url(token[..separator]);
            var signature = FromBase64Url(token[(separator + 1)..]);
            var expected = HMACSHA256.HashData(_key, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) return false;
            var parts = Encoding.UTF8.GetString(payloadBytes).Split('.');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var tokenAttachment) ||
                tokenAttachment != attachmentId || !Guid.TryParseExact(parts[1], "N", out accountId) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)) return false;
            return DateTimeOffset.FromUnixTimeSeconds(expires) > timeProvider.GetUtcNow();
        }
        catch (FormatException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value += new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(value);
    }
}
