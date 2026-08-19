using Iridium.Protocol;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Security;

public sealed class CommunityAuthorizationService
{
    public Task<bool> IsMemberAsync(Guid communityId, Guid accountId, IridiumDbContext db) =>
        db.CommunityMembers.AnyAsync(value => value.CommunityId == communityId && value.AccountId == accountId);

    public async Task<CommunityAccessDto> GetAccessAsync(Guid communityId, Guid accountId, IridiumDbContext db)
    {
        var ownerId = await db.Communities.Where(value => value.Id == communityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync();
        if (ownerId == accountId) return new CommunityAccessDto(true, CommunityPermission.All | CommunityPermission.Administrator);
        if (ownerId is null || !await IsMemberAsync(communityId, accountId, db))
            return new CommunityAccessDto(false, CommunityPermission.None);

        var roles = await db.CommunityRoles
            .Where(role => role.CommunityId == communityId &&
                (role.IsDefault || role.Members.Any(member => member.AccountId == accountId)))
            .Select(role => role.Permissions)
            .ToListAsync();
        var effective = roles.Aggregate(CommunityPermission.None, (current, value) => current | value);
        if ((effective & CommunityPermission.Administrator) != 0) effective |= CommunityPermission.All;
        return new CommunityAccessDto(false, effective);
    }

    public async Task<bool> HasPermissionAsync(
        Guid communityId, Guid accountId, CommunityPermission permission, IridiumDbContext db)
    {
        var access = await GetAccessAsync(communityId, accountId, db);
        return access.Has(permission);
    }

    // Kept as a compatibility convenience for existing callers; Community settings authority is distinct
    // from channel and message moderation permissions.
    public Task<bool> CanManageAsync(Guid communityId, Guid accountId, IridiumDbContext db) =>
        HasPermissionAsync(communityId, accountId, CommunityPermission.ManageCommunity, db);

    public async Task<int> HighestRolePositionAsync(Guid communityId, Guid accountId, IridiumDbContext db)
    {
        if (await db.Communities.AnyAsync(value => value.Id == communityId && value.OwnerAccountId == accountId))
            return int.MaxValue;
        return await db.CommunityMemberRoles
            .Where(value => value.CommunityId == communityId && value.AccountId == accountId)
            .Select(value => (int?)value.Role.Position).MaxAsync() ?? -1;
    }

    public async Task<bool> CanManageRoleAsync(
        Guid communityId, Guid accountId, Guid roleId, IridiumDbContext db)
    {
        var access = await GetAccessAsync(communityId, accountId, db);
        if (access.IsOwner) return true;
        if (!access.Has(CommunityPermission.ManageRoles)) return false;
        var role = await db.CommunityRoles
            .Where(value => value.CommunityId == communityId && value.Id == roleId)
            .Select(value => new { value.Position, value.IsDefault })
            .SingleOrDefaultAsync();
        return role is not null && !role.IsDefault && role.Position < await HighestRolePositionAsync(communityId, accountId, db);
    }

    public async Task<bool> CanSetMemberRolesAsync(
        Guid communityId, Guid actorAccountId, Guid targetAccountId,
        IReadOnlyCollection<Guid> requestedRoleIds, IridiumDbContext db)
    {
        var community = await db.Communities.AsNoTracking()
            .Where(value => value.Id == communityId)
            .Select(value => new { value.OwnerAccountId })
            .SingleOrDefaultAsync();
        if (community is null) return false;

        var access = await GetAccessAsync(communityId, actorAccountId, db);
        if (access.IsOwner) return true;
        if (!access.Has(CommunityPermission.ManageRoles) || targetAccountId == community.OwnerAccountId) return false;
        if (await HighestRolePositionAsync(communityId, targetAccountId, db) >=
            await HighestRolePositionAsync(communityId, actorAccountId, db)) return false;

        var existingRoleIds = await db.CommunityMemberRoles
            .Where(value => value.CommunityId == communityId && value.AccountId == targetAccountId)
            .Select(value => value.RoleId)
            .ToListAsync();
        var changedRoleIds = existingRoleIds.Except(requestedRoleIds)
            .Concat(requestedRoleIds.Except(existingRoleIds))
            .Distinct();
        foreach (var roleId in changedRoleIds)
            if (!await CanManageRoleAsync(communityId, actorAccountId, roleId, db)) return false;
        return true;
    }

    public async Task<bool> CanModerateMemberAsync(
        Guid communityId, Guid actorAccountId, Guid targetAccountId, CommunityPermission permission, IridiumDbContext db)
    {
        var community = await db.Communities.AsNoTracking()
            .Where(value => value.Id == communityId)
            .Select(value => new { value.OwnerAccountId })
            .SingleOrDefaultAsync();
        if (community is null || targetAccountId == community.OwnerAccountId) return false;
        var access = await GetAccessAsync(communityId, actorAccountId, db);
        if (!access.Has(permission)) return false;
        if (access.IsOwner) return true;
        return await HighestRolePositionAsync(communityId, actorAccountId, db) >
               await HighestRolePositionAsync(communityId, targetAccountId, db);
    }
}
