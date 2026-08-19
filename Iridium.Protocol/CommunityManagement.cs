namespace Iridium.Protocol;

[Flags]
public enum CommunityPermission : long
{
    None = 0,
    ViewChannels = 1L << 0,
    SendMessages = 1L << 1,
    ManageMessages = 1L << 2,
    ManageChannels = 1L << 3,
    ManageCommunity = 1L << 4,
    ManageRoles = 1L << 5,
    CreateInvites = 1L << 6,
    KickMembers = 1L << 7,
    BanMembers = 1L << 8,
    MentionEveryone = 1L << 9,
    Administrator = 1L << 62,
    All = ViewChannels | SendMessages | ManageMessages | ManageChannels | ManageCommunity |
          ManageRoles | CreateInvites | KickMembers | BanMembers | MentionEveryone
}

public sealed record CommunityAccessDto(bool IsOwner, CommunityPermission Permissions)
{
    public bool Has(CommunityPermission permission) =>
        IsOwner || (Permissions & CommunityPermission.Administrator) != 0 ||
        (Permissions & permission) == permission;
}

public sealed record CommunityRoleDto(
    Guid Id,
    Guid CommunityId,
    string Name,
    int Position,
    CommunityPermission Permissions,
    bool IsDefault,
    string? Color,
    bool DisplaySeparately,
    bool IsMentionable = false);

public sealed record CommunityMemberDto(
    Guid AccountId,
    string Username,
    string DisplayName,
    string? Pronouns,
    string? Description,
    string? Nickname,
    DateTimeOffset JoinedAt,
    bool IsOwner,
    PublicPresence Presence,
    IReadOnlyList<Guid> RoleIds);

public sealed record CommunityBanDto(
    Guid AccountId,
    string Username,
    string DisplayName,
    Guid BannedByAccountId,
    DateTimeOffset BannedAt,
    string? Reason);

public sealed record CommunityInviteDto(
    Guid Id,
    Guid CommunityId,
    string CodePrefix,
    string CreatorDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int? MaxUses,
    int Uses,
    bool Revoked,
    string? InviteUrl = null);

public sealed record CommunityManagementDto(
    CommunityDto Community,
    CommunityAccessDto Access,
    IReadOnlyList<CommunityRoleDto> Roles,
    IReadOnlyList<CommunityMemberDto> Members,
    IReadOnlyList<CommunityInviteDto> Invites,
    IReadOnlyList<CommunityBanDto> Bans);

public sealed record CreateCommunityRoleRequest(string Name, CommunityPermission Permissions, string? Color, bool DisplaySeparately = false, bool IsMentionable = false);
public sealed record UpdateCommunityRoleRequest(string Name, CommunityPermission Permissions, string? Color, bool DisplaySeparately = false, bool IsMentionable = false);
public sealed record MoveCommunityRoleRequest(int Position);
public sealed record SetCommunityMemberRolesRequest(IReadOnlyList<Guid> RoleIds);
public sealed record UpdateCommunityRequest(string Name, string? Description);
public sealed record CreateCommunityInviteRequest(DateTimeOffset? ExpiresAt, int? MaxUses);
public sealed record BanCommunityMemberRequest(string? Reason);

public enum CommunityInviteStatus
{
    Valid,
    Expired,
    Revoked,
    Exhausted,
    NotFound,
    AuthenticationRequiredOnTargetNode
}

public sealed record CommunityInvitePreviewDto(
    CommunityInviteStatus Status,
    string? CommunityName,
    string? CommunityIconUrl,
    string? CommunityBannerUrl,
    int MemberCount,
    string NodeAuthority,
    bool AlreadyMember,
    Guid? CommunityId);

public sealed record JoinCommunityInviteResultDto(
    CommunityDto Community,
    bool AlreadyMember);

public static class CommunityHubContract
{
    public const string StateChanged = "CommunityStateChanged";
    public const string AccessRevoked = "CommunityAccessRevoked";
}

public sealed record CommunityStateChangedEvent(Guid CommunityId, string Change);
public sealed record CommunityAccessRevokedEvent(Guid CommunityId, Guid AccountId, string Reason);

public sealed record CommunityInviteReference(string Token, string NodeAuthority, string OriginalUrl);

public static class CommunityInviteLink
{
    public static CommunityInviteReference? Find(string content)
    {
        foreach (var raw in content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = raw.Trim('(', ')', '[', ']', '<', '>', ',', '.', ';');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https" or "iridium")) continue;
            if (uri.Scheme == "iridium")
            {
                if (!string.Equals(uri.Host, "invite", StringComparison.OrdinalIgnoreCase)) continue;
                var customToken = uri.AbsolutePath.Trim('/');
                if (ValidToken(customToken)) return new(customToken, string.Empty, candidate);
                continue;
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2 || !string.Equals(segments[0], "invite", StringComparison.OrdinalIgnoreCase)) continue;
            if (ValidToken(segments[1])) return new(segments[1], uri.Authority, candidate);
        }
        return null;
    }

    private static bool ValidToken(string token) =>
        token.Length is >= 20 and <= 256 && token.All(value => char.IsLetterOrDigit(value) || value is '-' or '_');
}
