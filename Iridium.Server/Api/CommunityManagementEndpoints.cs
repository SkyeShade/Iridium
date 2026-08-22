using Iridium.Protocol;
using Iridium.Server.Configuration;
using Iridium.Server.Communities;
using Iridium.Server.Domain;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Api;

public static class CommunityManagementEndpoints
{
    private const CommunityPermission DefinedPermissions = CommunityPermission.All | CommunityPermission.Administrator;

    public static IEndpointRouteBuilder MapCommunityManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}");
        group.MapGet("/management", GetManagementAsync);
        group.MapPatch("/", UpdateCommunityAsync);
        group.MapPost("/roles", CreateRoleAsync);
        group.MapPatch("/roles/{roleId:guid}", UpdateRoleAsync);
        group.MapPost("/roles/{roleId:guid}/move", MoveRoleAsync);
        group.MapDelete("/roles/{roleId:guid}", DeleteRoleAsync);
        group.MapPut("/members/{accountId:guid}/roles", SetMemberRolesAsync);
        group.MapPost("/members/{accountId:guid}/kick", KickMemberAsync);
        group.MapPost("/bans/{accountId:guid}", BanMemberAsync);
        group.MapDelete("/bans/{accountId:guid}", UnbanMemberAsync);
        group.MapGet("/invites", ListInvitesAsync);
        group.MapPost("/invites", CreateInviteAsync);
        group.MapDelete("/invites/{inviteId:guid}", RevokeInviteAsync);

        endpoints.MapGet("/api/invites/{token}", ResolveInviteAsync);
        endpoints.MapPost("/api/invites/{token}/join", JoinInviteAsync);
        return endpoints;
    }

    private static async Task<IResult> GetManagementAsync(
        Guid communityId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, PresenceTracker presence,
        ICommunityLimitsService limitService)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.Forbid();

        var community = await db.Communities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == communityId);
        if (community is null) return Results.NotFound();
        var roles = await db.CommunityRoles.AsNoTracking().Where(value => value.CommunityId == communityId)
            .OrderByDescending(value => value.Position).ThenBy(value => value.Name).Select(value => ToDto(value)).ToListAsync();
        var members = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == communityId)
            .Include(value => value.Account).Include(value => value.Roles)
            .OrderBy(value => value.Account.DisplayName).ThenBy(value => value.Account.Username).ToListAsync();
        var memberDtos = members.Select(value => new CommunityMemberDto(
            value.AccountId, value.Account.Username, value.Account.DisplayName, value.Account.Pronouns,
            value.Account.Description, value.Nickname, value.JoinedAt,
            value.AccountId == community.OwnerAccountId, presence.GetPublic(value.AccountId),
            value.Roles.Select(role => role.RoleId).ToArray())).ToArray();

        var invites = access.Has(CommunityPermission.CreateInvites)
            ? await LoadInvitesAsync(communityId, db)
            : [];
        var bans = access.Has(CommunityPermission.BanMembers)
            ? await LoadBansAsync(communityId, db)
            : [];
        return Results.Ok(new CommunityManagementDto(ToDto(community), access, roles, memberDtos, invites, bans,
            limitService.GetEffectiveLimits(communityId)));
    }

    private static async Task<IResult> UpdateCommunityAsync(
        Guid communityId, UpdateCommunityRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.ManageCommunity, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var name = request.Name.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (name.Length is < 1 or > 100) return Validation(nameof(request.Name), "Community names must be between 1 and 100 characters.");
        if (description?.Length > 500) return Validation(nameof(request.Description), "Descriptions cannot exceed 500 characters.");
        var community = await db.Communities.SingleOrDefaultAsync(value => value.Id == communityId);
        if (community is null) return Results.NotFound();
        community.Name = name;
        community.Description = description;
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "overview", db);
        return Results.Ok(ToDto(community));
    }

    private static async Task<IResult> CreateRoleAsync(
        Guid communityId, CreateCommunityRoleRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.ManageRoles, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var error = ValidateRole(request.Name, request.Permissions, request.Color);
        if (error is not null) return error;
        if (!actor.Access!.IsOwner && (request.Permissions & ~actor.Access.Permissions) != 0) return Results.Forbid();
        var highest = await authorization.HighestRolePositionAsync(communityId, actor.AccountId, db);
        var currentMaximum = await db.CommunityRoles.Where(value => value.CommunityId == communityId)
            .Select(value => (int?)value.Position).MaxAsync() ?? 0;
        var position = actor.Access!.IsOwner ? currentMaximum + 1 : Math.Max(1, Math.Min(currentMaximum + 1, highest - 1));
        if (!actor.Access.IsOwner && highest <= 1)
            return Results.Problem("Your highest role does not allow creating a manageable role.", statusCode: StatusCodes.Status403Forbidden);
        var role = new CommunityRole
        {
            Id = Guid.NewGuid(), CommunityId = communityId, Community = null!, Name = request.Name.Trim(),
            Position = position, Permissions = request.Permissions, Color = NormalizeColor(request.Color),
            DisplaySeparately = request.DisplaySeparately
            , IsMentionable = request.IsMentionable
        };
        db.CommunityRoles.Add(role);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Results.Conflict(new { message = "A role with that name already exists." }); }
        await realtime.PublishAsync(communityId, "role-created", db);
        return Results.Created($"/api/communities/{communityId}/roles/{role.Id}", ToDto(role));
    }

    private static async Task<IResult> UpdateRoleAsync(
        Guid communityId, Guid roleId, UpdateCommunityRoleRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.ManageRoles, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var error = ValidateRole(request.Name, request.Permissions, request.Color);
        if (error is not null) return error;
        if (!actor.Access!.IsOwner && (request.Permissions & ~actor.Access.Permissions) != 0) return Results.Forbid();
        var role = await db.CommunityRoles.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == roleId);
        if (role is null) return Results.NotFound();
        if (!actor.Access!.IsOwner && !role.IsDefault && !await authorization.CanManageRoleAsync(communityId, actor.AccountId, roleId, db))
            return Results.Forbid();
        role.Name = role.IsDefault ? "@everyone" : request.Name.Trim();
        role.Permissions = request.Permissions;
        role.Color = NormalizeColor(request.Color);
        role.DisplaySeparately = !role.IsDefault && request.DisplaySeparately;
        role.IsMentionable = !role.IsDefault && request.IsMentionable;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Results.Conflict(new { message = "A role with that name already exists." }); }
        await realtime.PublishAsync(communityId, "role-updated", db);
        return Results.Ok(ToDto(role));
    }

    private static async Task<IResult> MoveRoleAsync(
        Guid communityId, Guid roleId, MoveCommunityRoleRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.ManageRoles, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var roles = await db.CommunityRoles.Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.Position).ToListAsync();
        var role = roles.SingleOrDefault(value => value.Id == roleId);
        if (role is null) return Results.NotFound();
        if (role.IsDefault) return Results.Problem("The default role must remain at the bottom.", statusCode: StatusCodes.Status409Conflict);
        if (!actor.Access!.IsOwner && !await authorization.CanManageRoleAsync(communityId, actor.AccountId, roleId, db)) return Results.Forbid();
        var maximum = actor.Access.IsOwner ? roles.Count - 1 : Math.Max(1, await authorization.HighestRolePositionAsync(communityId, actor.AccountId, db) - 1);
        roles.Remove(role);
        var custom = roles.Where(value => !value.IsDefault).OrderBy(value => value.Position).ToList();
        custom.Insert(Math.Clamp(request.Position - 1, 0, Math.Min(custom.Count, maximum - 1)), role);
        var defaultRole = roles.Single(value => value.IsDefault);
        defaultRole.Position = 0;
        for (var index = 0; index < custom.Count; index++) custom[index].Position = index + 1;
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "roles-reordered", db);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteRoleAsync(
        Guid communityId, Guid roleId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.ManageRoles, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var role = await db.CommunityRoles.Include(value => value.Members)
            .SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == roleId);
        if (role is null) return Results.NotFound();
        if (role.IsDefault) return Results.Problem("The @everyone role cannot be deleted.", statusCode: StatusCodes.Status409Conflict);
        if (!actor.Access!.IsOwner && !await authorization.CanManageRoleAsync(communityId, actor.AccountId, roleId, db)) return Results.Forbid();
        db.CommunityMemberRoles.RemoveRange(role.Members);
        db.CommunityRoles.Remove(role);
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "role-deleted", db);
        return Results.NoContent();
    }

    private static async Task<IResult> SetMemberRolesAsync(
        Guid communityId, Guid accountId, SetCommunityMemberRolesRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.ManageRoles, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var community = await db.Communities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == communityId);
        if (community is null) return Results.NotFound();
        if (!actor.Access!.IsOwner && accountId == community.OwnerAccountId) return Results.Forbid();
        var member = await db.CommunityMembers.Include(value => value.Roles)
            .SingleOrDefaultAsync(value => value.CommunityId == communityId && value.AccountId == accountId);
        if (member is null) return Results.NotFound();
        var roleIds = request.RoleIds.Distinct().ToArray();
        var roles = await db.CommunityRoles.Where(value => value.CommunityId == communityId && roleIds.Contains(value.Id) && !value.IsDefault).ToListAsync();
        if (roles.Count != roleIds.Length) return Results.BadRequest(new { message = "One or more roles do not belong to this Community." });
        if (!await authorization.CanSetMemberRolesAsync(communityId, actor.AccountId, accountId, roleIds, db))
            return Results.Forbid();
        db.CommunityMemberRoles.RemoveRange(member.Roles);
        foreach (var role in roles)
            db.CommunityMemberRoles.Add(new CommunityMemberRole
            {
                CommunityId = communityId, AccountId = accountId, RoleId = role.Id, Member = member, Role = role
            });
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "member-roles", db);
        return Results.NoContent();
    }

    private static Task<IResult> KickMemberAsync(
        Guid communityId, Guid accountId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime) =>
        RemoveMemberAsync(communityId, accountId, false, null, context, db, sessions, authorization, realtime);

    private static Task<IResult> BanMemberAsync(
        Guid communityId, Guid accountId, BanCommunityMemberRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime) =>
        RemoveMemberAsync(communityId, accountId, true, request.Reason, context, db, sessions, authorization, realtime);

    private static async Task<IResult> RemoveMemberAsync(
        Guid communityId, Guid accountId, bool ban, string? reason, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var permission = ban ? CommunityPermission.BanMembers : CommunityPermission.KickMembers;
        var actor = await RequirePermissionAsync(communityId, permission, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var community = await db.Communities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == communityId);
        if (community is null) return Results.NotFound();
        if (accountId == community.OwnerAccountId)
            return Results.Problem("The Community owner cannot be kicked or banned.", statusCode: StatusCodes.Status409Conflict);
        var member = await db.CommunityMembers.Include(value => value.Roles)
            .SingleOrDefaultAsync(value => value.CommunityId == communityId && value.AccountId == accountId);
        if (member is null && !ban) return Results.NotFound();
        if (!await authorization.CanModerateMemberAsync(communityId, actor.AccountId, accountId, permission, db)) return Results.Forbid();
        if (ban)
        {
            var existing = await db.CommunityBans.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.AccountId == accountId);
            if (existing is null)
                db.CommunityBans.Add(new CommunityBan
                {
                    CommunityId = communityId, AccountId = accountId, BannedByAccountId = actor.AccountId,
                    BannedAt = DateTimeOffset.UtcNow, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)],
                    Community = null!, Account = null!, BannedByAccount = null!
                });
        }
        if (member is not null)
        {
            db.CommunityMemberRoles.RemoveRange(member.Roles);
            db.CommunityMembers.Remove(member);
        }
        await db.SaveChangesAsync();
        await realtime.PublishAccessRevokedAsync(
            new CommunityAccessRevokedEvent(communityId, accountId, ban ? "banned" : "kicked"));
        await realtime.PublishAsync(communityId, ban ? "member-banned" : "member-kicked", db);
        return Results.NoContent();
    }

    private static async Task<IResult> UnbanMemberAsync(
        Guid communityId, Guid accountId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.BanMembers, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var ban = await db.CommunityBans.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.AccountId == accountId);
        if (ban is null) return Results.NotFound();
        db.CommunityBans.Remove(ban);
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "member-unbanned", db);
        return Results.NoContent();
    }

    private static async Task<IResult> ListInvitesAsync(
        Guid communityId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.CreateInvites, context, db, sessions, authorization);
        return actor.Error ?? Results.Ok(await LoadInvitesAsync(communityId, db));
    }

    private static async Task<IResult> CreateInviteAsync(
        Guid communityId, CreateCommunityInviteRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.CreateInvites, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAt <= now) return Validation(nameof(request.ExpiresAt), "Expiration must be in the future.");
        if (request.MaxUses is <= 0 or > 1_000_000) return Validation(nameof(request.MaxUses), "Maximum uses must be between 1 and 1,000,000.");
        if (!await db.Communities.AnyAsync(value => value.Id == communityId)) return Results.NotFound();
        var token = InviteTokenService.CreateToken();
        var invite = new CommunityInvite
        {
            Id = Guid.NewGuid(), CommunityId = communityId, Community = null!, TokenHash = InviteTokenService.Hash(token),
            CodePrefix = InviteTokenService.Prefix(token), CreatedByAccountId = actor.AccountId,
            CreatedByAccount = null!, CreatedAt = now, ExpiresAt = request.ExpiresAt, MaxUses = request.MaxUses
        };
        db.CommunityInvites.Add(invite);
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "invite-created", db);
        var url = $"{context.Request.Scheme}://{context.Request.Host}/invite/{token}";
        return Results.Created($"/api/communities/{communityId}/invites/{invite.Id}",
            new CommunityInviteDto(invite.Id, communityId, invite.CodePrefix, actor.DisplayName!, invite.CreatedAt,
                invite.ExpiresAt, invite.MaxUses, invite.Uses, invite.Revoked, url));
    }

    private static async Task<IResult> RevokeInviteAsync(
        Guid communityId, Guid inviteId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var actor = await RequirePermissionAsync(communityId, CommunityPermission.CreateInvites, context, db, sessions, authorization);
        if (actor.Error is not null) return actor.Error;
        var invite = await db.CommunityInvites.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == inviteId);
        if (invite is null) return Results.NotFound();
        invite.Revoked = true;
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "invite-revoked", db);
        return Results.NoContent();
    }

    private static async Task<IResult> ResolveInviteAsync(
        string token, HttpContext context, IridiumDbContext db, SessionService sessions, IOptions<NodeOptions> options,
        CommunityInviteService inviteService)
    {
        var invite = await inviteService.FindAsync(token, db);
        var status = InviteTokenService.GetStatus(invite, DateTimeOffset.UtcNow);
        if (invite is null || status != CommunityInviteStatus.Valid)
            return Results.Ok(new CommunityInvitePreviewDto(status, null, null, null, 0,
                NodeAuthority(context, options.Value), false, null));
        var session = await sessions.GetAsync(context, db);
        var alreadyMember = session is not null && await db.CommunityMembers.AnyAsync(value =>
            value.CommunityId == invite.CommunityId && value.AccountId == session.AccountId);
        var count = await db.CommunityMembers.CountAsync(value => value.CommunityId == invite.CommunityId);
        return Results.Ok(new CommunityInvitePreviewDto(status, invite.Community.Name, null, null, count,
            NodeAuthority(context, options.Value), alreadyMember, invite.CommunityId));
    }

    private static async Task<IResult> JoinInviteAsync(
        string token, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityInviteService inviteService, CommunityRealtimePublisher realtime)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        CommunityInviteJoinOutcome outcome;
        try { outcome = await inviteService.JoinAsync(token, session.Account, db); }
        catch (CommunityInviteJoinException exception)
        {
            if (exception.Status == CommunityInviteStatus.NotFound) return Results.NotFound();
            return Results.Problem(exception.Message, statusCode: exception.Status is null
                ? StatusCodes.Status403Forbidden : StatusCodes.Status410Gone);
        }
        if (!outcome.AlreadyMember) await realtime.PublishAsync(outcome.Community.Id, "member-joined", db);
        return Results.Ok(new JoinCommunityInviteResultDto(ToDto(outcome.Community), outcome.AlreadyMember));
    }

    private static async Task<List<CommunityInviteDto>> LoadInvitesAsync(Guid communityId, IridiumDbContext db)
    {
        var invites = await db.CommunityInvites.AsNoTracking()
            .Where(value => value.CommunityId == communityId && !value.Revoked)
            .Select(value => new CommunityInviteDto(value.Id, value.CommunityId, value.CodePrefix,
                value.CreatedByAccount.DisplayName, value.CreatedAt, value.ExpiresAt, value.MaxUses, value.Uses,
                value.Revoked, null))
            .ToListAsync();

        return invites.OrderByDescending(value => value.CreatedAt).ToList();
    }

    private static async Task<List<CommunityBanDto>> LoadBansAsync(Guid communityId, IridiumDbContext db)
    {
        var bans = await db.CommunityBans.AsNoTracking()
            .Where(value => value.CommunityId == communityId)
            .Select(value => new CommunityBanDto(value.AccountId, value.Account.Username, value.Account.DisplayName,
                value.BannedByAccountId, value.BannedAt, value.Reason))
            .ToListAsync();

        return bans.OrderByDescending(value => value.BannedAt).ToList();
    }

    private static async Task<(Guid AccountId, string? DisplayName, CommunityAccessDto? Access, IResult? Error)> RequirePermissionAsync(
        Guid communityId, CommunityPermission permission, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return (Guid.Empty, null, null, Results.Unauthorized());
        var access = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!access.Has(permission)) return (session.AccountId, session.Account.DisplayName, access, Results.Forbid());
        return (session.AccountId, session.Account.DisplayName, access, null);
    }

    private static IResult? ValidateRole(string name, CommunityPermission permissions, string? color)
    {
        if (name.Trim().Length is < 1 or > 64) return Validation(nameof(name), "Role names must be between 1 and 64 characters.");
        if ((permissions & ~DefinedPermissions) != 0) return Validation(nameof(permissions), "The role contains unsupported permissions.");
        if (NormalizeColor(color) is null && !string.IsNullOrWhiteSpace(color)) return Validation(nameof(color), "Role colors must use #RRGGBB format.");
        return null;
    }

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var normalized = color.Trim().ToUpperInvariant();
        return normalized.Length == 7 && normalized[0] == '#' && normalized[1..].All(Uri.IsHexDigit) ? normalized : null;
    }

    private static CommunityRoleDto ToDto(CommunityRole role) =>
        new(role.Id, role.CommunityId, role.Name, role.Position, role.Permissions, role.IsDefault, role.Color,
            role.DisplaySeparately,
            role.IsMentionable);
    private static CommunityDto ToDto(Community community) =>
        new(community.Id, community.Name, community.Description, community.OwnerAccountId, community.CreatedAt);
    private static IResult Validation(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
    private static string NodeAuthority(HttpContext context, NodeOptions options) =>
        string.IsNullOrWhiteSpace(options.PublicAuthority) ? context.Request.Host.Value ?? "localhost" : options.PublicAuthority!;
}
