namespace Iridium.Protocol;

public static class CommunityRolePresentation
{
    public static string? MemberColor(CommunityMemberDto member, IEnumerable<CommunityRoleDto> roles) => roles
        .Where(role => member.RoleIds.Contains(role.Id) && !string.IsNullOrWhiteSpace(role.Color))
        .OrderByDescending(role => role.Position)
        .FirstOrDefault()?.Color;

    public static string? RoleColor(Guid roleId, IEnumerable<CommunityRoleDto> roles) =>
        roles.FirstOrDefault(role => role.Id == roleId)?.Color;
}
