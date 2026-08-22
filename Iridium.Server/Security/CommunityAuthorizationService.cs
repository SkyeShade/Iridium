using Iridium.Protocol;
using Iridium.Server.Domain;
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

    public async Task<CommunityAccessDto> GetChannelAccessAsync(
        Guid communityId, Guid channelId, Guid accountId, IridiumDbContext db)
    {
        var baseAccess = await GetAccessAsync(communityId, accountId, db);
        if (baseAccess.IsOwner || baseAccess.Has(CommunityPermission.Administrator)) return baseAccess;
        var channel = await db.CommunityChannels.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.Id == channelId)
            .Select(value => new { value.CategoryId, value.PermissionsSyncedToCategory })
            .SingleOrDefaultAsync();
        if (channel is null) return new(false, CommunityPermission.None);

        var scopeType = channel.PermissionsSyncedToCategory && channel.CategoryId.HasValue
            ? PermissionOverwriteScopeType.Category : PermissionOverwriteScopeType.Channel;
        var scopeId = scopeType == PermissionOverwriteScopeType.Category ? channel.CategoryId!.Value : channelId;
        var overwrites = await db.CommunityPermissionOverwrites.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ScopeType == scopeType && value.ScopeId == scopeId)
            .ToListAsync();
        var roleIds = await db.CommunityMemberRoles.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.AccountId == accountId)
            .Select(value => value.RoleId).ToListAsync();
        return new(false, Resolve(baseAccess.Permissions, overwrites, roleIds, accountId));
    }

    public async Task<CommunityAccessDto> GetCategoryAccessAsync(Guid communityId, Guid categoryId, Guid accountId,
        IridiumDbContext db)
    {
        var baseAccess = await GetAccessAsync(communityId, accountId, db);
        if (baseAccess.IsOwner || baseAccess.Has(CommunityPermission.Administrator)) return baseAccess;
        if (!await db.CommunityCategories.AnyAsync(value => value.CommunityId == communityId && value.Id == categoryId))
            return new(false, CommunityPermission.None);
        var overwrites = await db.CommunityPermissionOverwrites.AsNoTracking().Where(value =>
            value.CommunityId == communityId && value.ScopeType == PermissionOverwriteScopeType.Category &&
            value.ScopeId == categoryId).ToListAsync();
        var roleIds = await db.CommunityMemberRoles.AsNoTracking().Where(value =>
            value.CommunityId == communityId && value.AccountId == accountId).Select(value => value.RoleId).ToListAsync();
        return new(false, Resolve(baseAccess.Permissions, overwrites, roleIds, accountId));
    }

    public async Task<bool> HasChannelPermissionAsync(Guid communityId, Guid channelId, Guid accountId,
        CommunityPermission permission, IridiumDbContext db) =>
        (await GetChannelAccessAsync(communityId, channelId, accountId, db)).Has(permission);

    public static CommunityPermission Resolve(CommunityPermission basePermissions,
        IEnumerable<CommunityPermissionOverwrite> overwrites, IReadOnlyCollection<Guid> roleIds, Guid accountId)
    {
        var values = overwrites.ToArray();
        var effective = Apply(basePermissions,
            values.FirstOrDefault(value => value.TargetType == PermissionOverwriteTargetType.Everyone));
        var roleValues = values.Where(value => value.TargetType == PermissionOverwriteTargetType.Role &&
                                               value.TargetId is { } id && roleIds.Contains(id)).ToArray();
        var roleDeny = roleValues.Aggregate(CommunityPermission.None, (bits, value) => bits | value.Deny);
        var roleAllow = roleValues.Aggregate(CommunityPermission.None, (bits, value) => bits | value.Allow);
        effective = (effective & ~roleDeny) | roleAllow;
        effective = Apply(effective, values.FirstOrDefault(value =>
            value.TargetType == PermissionOverwriteTargetType.Member && value.TargetId == accountId));
        return effective;
    }

    private static CommunityPermission Apply(CommunityPermission permissions, CommunityPermissionOverwrite? overwrite) =>
        overwrite is null ? permissions : (permissions & ~overwrite.Deny) | overwrite.Allow;

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
