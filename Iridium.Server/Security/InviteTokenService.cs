using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Iridium.Server.Security;

using Iridium.Protocol;
using Iridium.Server.Domain;

public static class InviteTokenService
{
    public static string CreateToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string Prefix(string token) => token[..Math.Min(8, token.Length)];

    public static CommunityInviteStatus GetStatus(CommunityInvite? invite, DateTimeOffset now)
    {
        if (invite is null) return CommunityInviteStatus.NotFound;
        if (invite.Revoked) return CommunityInviteStatus.Revoked;
        if (invite.ExpiresAt is { } expires && expires <= now) return CommunityInviteStatus.Expired;
        if (invite.MaxUses is { } maximum && invite.Uses >= maximum) return CommunityInviteStatus.Exhausted;
        return CommunityInviteStatus.Valid;
    }
}
