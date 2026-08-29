using System.Data;
using System.Text.RegularExpressions;
using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Communities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Iridium.Server.Api;

public static partial class CommunityStructureEndpoints
{
    internal const int MaximumCategoryDepth = 5;
    private const CommunityPermission ChannelPermissions = CommunityPermission.ViewChannels |
        CommunityPermission.SendMessages | CommunityPermission.ManageMessages | CommunityPermission.ManageChannels |
        CommunityPermission.ManagePermissions | CommunityPermission.CreateInvites |
        CommunityPermission.ReadMessageHistory | CommunityPermission.AttachFiles | CommunityPermission.EmbedLinks |
        CommunityPermission.AddReactions | CommunityPermission.UseExternalEmoji | CommunityPermission.MentionEveryone | CommunityPermission.ConnectVoice |
        CommunityPermission.SpeakVoice | CommunityPermission.ShareScreen | CommunityPermission.MuteMembers |
        CommunityPermission.DeafenMembers | CommunityPermission.MoveMembers | CommunityPermission.CreateForumPosts;

    public static IEndpointRouteBuilder MapCommunityStructureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}");
        group.MapGet("/structure", GetStructureAsync);
        group.MapPost("/categories", CreateCategoryAsync);
        group.MapPatch("/categories/{categoryId:guid}", UpdateCategoryAsync);
        group.MapPost("/categories/{categoryId:guid}/move", MoveCategoryAsync);
        group.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync);
        group.MapPost("/channels", CreateChannelAsync);
        group.MapPatch("/channels/{channelId:guid}", UpdateChannelAsync);
        group.MapPost("/channels/{channelId:guid}/move", MoveChannelAsync);
        group.MapDelete("/channels/{channelId:guid}", DeleteChannelAsync);
        group.MapGet("/permissions/{scopeType}/{scopeId:guid}", GetPermissionScopeAsync);
        group.MapPut("/permissions/{scopeType}/{scopeId:guid}", ReplacePermissionOverwritesAsync);
        group.MapPut("/permissions/{scopeType}/{scopeId:guid}/overwrites", SetPermissionOverwriteAsync);
        group.MapPost("/permissions/{scopeType}/{scopeId:guid}/overwrites/remove", RemovePermissionOverwriteAsync);
        group.MapPost("/channels/{channelId:guid}/permissions/sync", SyncChannelPermissionsAsync);
        return endpoints;
    }

    private static async Task<IResult> GetStructureAsync(Guid communityId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!access.IsOwner && !await authorization.IsMemberAsync(communityId, session.AccountId, db))
            return Results.Forbid();

        var categoryEntities = await db.CommunityCategories.AsNoTracking().Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.ParentCategoryId).ThenBy(value => value.Position).ThenBy(value => value.Name)
            .ToListAsync();
        var channelEntities = await db.CommunityChannels.AsNoTracking().Where(value =>
                value.CommunityId == communityId && value.ParentForumChannelId == null)
            .OrderBy(value => value.CategoryId).ThenBy(value => value.Position).ThenBy(value => value.Name)
            .ToListAsync();
        var overwriteRows = await db.CommunityPermissionOverwrites.AsNoTracking()
            .Where(value => value.CommunityId == communityId).ToListAsync();
        var overwriteCategoryIds = overwriteRows.Where(value => value.ScopeType == PermissionOverwriteScopeType.Category)
            .Select(value => value.ScopeId).Distinct().ToHashSet();
        var privateCategoryIds = overwriteRows.Where(value => value.ScopeType == PermissionOverwriteScopeType.Category &&
                value.TargetType == PermissionOverwriteTargetType.Everyone &&
                (value.Deny & CommunityPermission.ViewChannels) != 0)
            .Select(value => value.ScopeId).ToHashSet();
        var privateChannelIds = overwriteRows.Where(value => value.ScopeType == PermissionOverwriteScopeType.Channel &&
                value.TargetType == PermissionOverwriteTargetType.Everyone &&
                (value.Deny & CommunityPermission.ViewChannels) != 0)
            .Select(value => value.ScopeId).ToHashSet();
        var categories = categoryEntities.Select(value => ToDto(value) with
            {
                HasPermissionOverwrites = overwriteCategoryIds.Contains(value.Id),
                IsPrivate = privateCategoryIds.Contains(value.Id)
            }).ToList();
        var categoryVisibility = new Dictionary<Guid, bool>();
        for (var index = 0; index < categories.Count; index++)
        {
            var categoryAccess = await authorization.GetCategoryAccessAsync(communityId, categories[index].Id,
                session.AccountId, db);
            categories[index] = categories[index] with { EffectivePermissions = categoryAccess.Permissions };
            categoryVisibility[categories[index].Id] = categoryAccess.Has(CommunityPermission.ViewChannels);
        }
        var channels = new List<CommunityChannelDto>();
        foreach (var entity in channelEntities)
        {
            var channelAccess = await authorization.GetChannelAccessAsync(communityId, entity.Id, session.AccountId, db);
            if (!channelAccess.Has(CommunityPermission.ViewChannels)) continue;
            if (entity.CategoryId is { } categoryId && !AncestorsVisible(categoryId, categoryVisibility, categoryEntities))
                continue;
            var isPrivate = entity.PermissionsSyncedToCategory && entity.CategoryId is { } syncedCategoryId
                ? privateCategoryIds.Contains(syncedCategoryId)
                : privateChannelIds.Contains(entity.Id);
            channels.Add(ToDto(entity) with { EffectivePermissions = channelAccess.Permissions, IsPrivate = isPrivate });
        }
        var visibleCategories = new HashSet<Guid>(channels.Where(value => value.CategoryId.HasValue)
            .Select(value => value.CategoryId!.Value));
        var added = true;
        while (added)
        {
            added = false;
            foreach (var category in categories.Where(value => visibleCategories.Contains(value.Id) && value.ParentCategoryId.HasValue))
                added |= visibleCategories.Add(category.ParentCategoryId!.Value);
        }
        if (!access.IsOwner && !access.Has(CommunityPermission.Administrator))
            categories.RemoveAll(value => !visibleCategories.Contains(value.Id));
        var readStates = await db.CommunityChannelReadStates.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.AccountId == session.AccountId)
            .ToDictionaryAsync(value => value.ChannelId, value => value.LastReadAt);
        for (var index = 0; index < channels.Count; index++)
        {
            var channel = channels[index];
            readStates.TryGetValue(channel.Id, out var lastReadAt);
            var activityChannelIds = channel.Kind == CommunityChannelKind.Forum
                ? await db.CommunityForumPosts.Where(value => value.CommunityId == communityId &&
                        value.ForumChannelId == channel.Id).Select(value => value.DiscussionChannelId).ToListAsync()
                : [channel.Id];
            var unread = channel.Kind == CommunityChannelKind.Forum
                ? await db.ChannelMessages.CountAsync(message => message.CommunityId == communityId &&
                    activityChannelIds.Contains(message.ChannelId) && message.AuthorAccountId != session.AccountId &&
                    !db.CommunityChannelReadStates.Any(state => state.CommunityId == communityId &&
                        state.ChannelId == message.ChannelId && state.AccountId == session.AccountId &&
                        state.LastReadAt >= message.CreatedAt))
                : await db.ChannelMessages.CountAsync(value => value.CommunityId == communityId &&
                    value.ChannelId == channel.Id && value.AuthorAccountId != session.AccountId && value.CreatedAt > lastReadAt);
            var mentions = await db.CommunityMentionNotifications.CountAsync(value => value.AccountId == session.AccountId &&
                value.CommunityId == communityId && activityChannelIds.Contains(value.ChannelId) && value.ReadAt == null);
            channels[index] = channel with { UnreadCount = unread, MentionCount = mentions };
        }
        return Results.Ok(new CommunityStructureDto(communityId, access.Has(CommunityPermission.ManageChannels),
            categories, channels, access.Permissions, access.IsOwner,
            access.Has(CommunityPermission.ManagePermissions)));
    }

    private static bool AncestorsVisible(Guid categoryId, IReadOnlyDictionary<Guid, bool> visibility,
        IReadOnlyList<CommunityCategory> categories)
    {
        var currentId = (Guid?)categoryId;
        var visited = new HashSet<Guid>();
        while (currentId is { } id && visited.Add(id))
        {
            if (!visibility.GetValueOrDefault(id)) return false;
            currentId = categories.FirstOrDefault(value => value.Id == id)?.ParentCategoryId;
        }
        return true;
    }

    private static async Task<IResult> CreateCategoryAsync(Guid communityId, CreateCategoryRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        if (ValidateName(request.Name, "Category") is { } nameError) return nameError;
        if (!await db.Communities.AnyAsync(value => value.Id == communityId)) return Results.NotFound();
        var categories = await LoadCategoriesAsync(communityId, db);
        if (request.ParentCategoryId is { } parentId)
        {
            var parent = categories.SingleOrDefault(value => value.Id == parentId);
            if (parent is null) return Invalid("The parent Category does not belong to this Server.");
            if (Depth(parent, categories) >= MaximumCategoryDepth)
                return Invalid($"Categories may be nested at most {MaximumCategoryDepth} levels deep.");
        }
        var category = new CommunityCategory
        {
            Id = Guid.NewGuid(), CommunityId = communityId, Name = request.Name.Trim(),
            ParentCategoryId = request.ParentCategoryId,
            Position = categories.Count(value => value.ParentCategoryId == request.ParentCategoryId) +
                       await db.CommunityChannels.CountAsync(value => value.CommunityId == communityId &&
                           value.CategoryId == request.ParentCategoryId && value.ParentForumChannelId == null), Community = null!
        };
        db.CommunityCategories.Add(category);
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "category-created", db);
        return Results.Created($"/api/communities/{communityId}/categories/{category.Id}", ToDto(category));
    }

    private static async Task<IResult> UpdateCategoryAsync(Guid communityId, Guid categoryId,
        UpdateCategoryRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        if (ValidateName(request.Name, "Category") is { } nameError) return nameError;
        var category = await db.CommunityCategories.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.Id == categoryId);
        if (category is null) return Results.NotFound();
        category.Name = request.Name.Trim();
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "category-updated", db);
        return Results.Ok(ToDto(category));
    }

    private static async Task<IResult> MoveCategoryAsync(Guid communityId, Guid categoryId,
        CommunitySidebarMoveRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var categories = await LoadCategoriesAsync(communityId, db);
            var channels = await LoadChannelsAsync(communityId, db);
            var category = categories.SingleOrDefault(value => value.Id == categoryId);
            if (category is null) return Results.NotFound();
            var items = SidebarItems(categories, channels);
            if (ResolveDestination(items, new SidebarItem(category), request) is not { } destination)
                return Invalid("The requested Category destination is no longer available.");
            if (destination.ParentCategoryId == categoryId) return Invalid("A Category cannot contain itself.");
            if (destination.ParentCategoryId is { } parentId)
            {
                var parent = categories.SingleOrDefault(value => value.Id == parentId);
                if (parent is null) return Invalid("The destination Category does not belong to this Server.");
                if (Descendants(category.Id, categories).Contains(parentId))
                    return Invalid("A Category cannot be moved into one of its descendants.");
                var destinationDepth = Depth(parent, categories) + 1;
                if (destinationDepth + SubtreeHeight(category.Id, categories) - 1 > MaximumCategoryDepth)
                    return Invalid($"That move would exceed the maximum Category depth of {MaximumCategoryDepth}.");
            }

            ApplySidebarMove(items, new SidebarItem(category), destination);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await realtime.PublishAsync(communityId, "category-moved", db);
            return Results.NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return CategoryMoveConflict();
        }
        catch (DbUpdateException exception) when (IsSqliteWriteConflict(exception))
        {
            return CategoryMoveConflict();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return CategoryMoveConflict();
        }
    }

    private static async Task<IResult> DeleteCategoryAsync(Guid communityId, Guid categoryId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var category = await db.CommunityCategories.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.Id == categoryId);
        if (category is null) return Results.NotFound();
        if (await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.CategoryId == categoryId) ||
            await db.CommunityCategories.AnyAsync(value => value.CommunityId == communityId && value.ParentCategoryId == categoryId))
            return Results.Conflict(new { message = "Move or delete this Category's channels and subcategories first." });
        var parent = category.ParentCategoryId;
        var permissionRows = await db.CommunityPermissionOverwrites.Where(value => value.CommunityId == communityId &&
            value.ScopeType == PermissionOverwriteScopeType.Category && value.ScopeId == categoryId).ToListAsync();
        db.CommunityPermissionOverwrites.RemoveRange(permissionRows);
        db.CommunityCategories.Remove(category);
        await db.SaveChangesAsync();
        await NormalizeSidebarPositionsAsync(communityId, parent, db);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await realtime.PublishAsync(communityId, "category-deleted", db);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateChannelAsync(Guid communityId, CreateChannelRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        if (!Enum.IsDefined(request.Kind)) return Invalid("That Channel type is not supported.");
        var name = NormalizeChannelName(request.Name);
        if (name is null) return Invalid("Channel names must be 1-100 characters using letters, numbers, underscores, or hyphens.");
        CommunityCategory? category = null;
        if (request.CategoryId is { } categoryId)
        {
            category = await db.CommunityCategories.SingleOrDefaultAsync(value =>
                value.CommunityId == communityId && value.Id == categoryId);
            if (category is null) return Invalid("The selected Category does not belong to this Server.");
        }
        var channel = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = communityId, CategoryId = request.CategoryId, Category = category,
            Name = name, Kind = request.Kind,
            Position = await db.CommunityChannels.CountAsync(value => value.CommunityId == communityId && value.CategoryId == request.CategoryId && value.ParentForumChannelId == null) +
                       await db.CommunityCategories.CountAsync(value => value.CommunityId == communityId && value.ParentCategoryId == request.CategoryId),
            CreatedAt = DateTimeOffset.UtcNow, Community = null!
            , PermissionsSyncedToCategory = request.CategoryId.HasValue
        };
        db.CommunityChannels.Add(channel);
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "channel-created", db);
        return Results.Created($"/api/communities/{communityId}/channels/{channel.Id}", ToDto(channel));
    }

    private static async Task<IResult> UpdateChannelAsync(Guid communityId, Guid channelId,
        UpdateChannelRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        if (!Enum.IsDefined(request.Kind)) return Invalid("That Channel type is not supported.");
        var name = NormalizeChannelName(request.Name);
        if (name is null) return Invalid("Enter a valid Channel name.");
        if (request.CategoryId is { } categoryId && !await CategoryExistsAsync(communityId, categoryId, db))
            return Invalid("The selected Category does not belong to this Server.");
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        channel.Name = name;
        channel.Kind = request.Kind;
        if (request.RequireTag.HasValue)
        {
            if (channel.Kind != CommunityChannelKind.Forum && request.RequireTag.Value)
                return Invalid("Only Forum Channels can require tags.");
            channel.RequireTag = channel.Kind == CommunityChannelKind.Forum && request.RequireTag.Value;
        }
        if (channel.CategoryId != request.CategoryId)
        {
            if (channel.PermissionsSyncedToCategory && request.CategoryId is null)
                channel.PermissionsSyncedToCategory = false;
            var categories = await LoadCategoriesAsync(communityId, db);
            var channels = await LoadChannelsAsync(communityId, db);
            ApplySidebarMove(SidebarItems(categories, channels), new SidebarItem(channel),
                new SidebarDestination(request.CategoryId, int.MaxValue));
        }
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "channel-updated", db);
        return Results.Ok(ToDto(channel));
    }

    private static async Task<IResult> MoveChannelAsync(Guid communityId, Guid channelId, CommunitySidebarMoveRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var categories = await LoadCategoriesAsync(communityId, db);
            var channels = await LoadChannelsAsync(communityId, db);
            var channel = channels.SingleOrDefault(value => value.Id == channelId);
            if (channel is null) return Results.NotFound();
            var items = SidebarItems(categories, channels);
            if (ResolveDestination(items, new SidebarItem(channel), request) is not { } destination)
                return Invalid("The requested Channel destination is no longer available.");
            ApplySidebarMove(items, new SidebarItem(channel), destination);
            if (channel.PermissionsSyncedToCategory && destination.ParentCategoryId is null)
                channel.PermissionsSyncedToCategory = false;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await realtime.PublishAsync(communityId, "channel-moved", db);
            return Results.NoContent();
        }
        catch (DbUpdateConcurrencyException) { return CategoryMoveConflict(); }
        catch (DbUpdateException exception) when (IsSqliteWriteConflict(exception)) { return CategoryMoveConflict(); }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return CategoryMoveConflict(); }
    }

    private static async Task<IResult> DeleteChannelAsync(Guid communityId, Guid channelId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        var categoryId = channel.CategoryId;
        var permissionRows = await db.CommunityPermissionOverwrites.Where(value => value.CommunityId == communityId &&
            value.ScopeType == PermissionOverwriteScopeType.Channel && value.ScopeId == channelId).ToListAsync();
        db.CommunityPermissionOverwrites.RemoveRange(permissionRows);
        if (channel.Kind == CommunityChannelKind.Forum)
        {
            var posts = await db.CommunityForumPosts.Where(value => value.CommunityId == communityId &&
                value.ForumChannelId == channelId).ToListAsync();
            var discussionIds = posts.Select(value => value.DiscussionChannelId).ToArray();
            db.CommunityForumPosts.RemoveRange(posts);
            await db.SaveChangesAsync();
            var discussions = await db.CommunityChannels.Where(value => value.CommunityId == communityId &&
                discussionIds.Contains(value.Id)).ToListAsync();
            db.CommunityChannels.RemoveRange(discussions);
        }
        db.CommunityChannels.Remove(channel);
        await db.SaveChangesAsync();
        await NormalizeSidebarPositionsAsync(communityId, categoryId, db);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await realtime.PublishAsync(communityId, "channel-deleted", db);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPermissionScopeAsync(Guid communityId, string scopeType, Guid scopeId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!TryScope(scopeType, out var type) || !await ScopeExistsAsync(communityId, type, scopeId, db))
            return Results.NotFound();
        if (!await CanManagePermissionsAsync(communityId, type, scopeId, session.AccountId, db, authorization))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var queryType = type;
        var queryScopeId = scopeId;
        var synced = false;
        if (type == PermissionOverwriteScopeType.Channel)
        {
            var channelScope = await db.CommunityChannels.AsNoTracking().Where(value =>
                    value.CommunityId == communityId && value.Id == scopeId)
                .Select(value => new { value.CategoryId, value.PermissionsSyncedToCategory }).SingleAsync();
            synced = channelScope.PermissionsSyncedToCategory && channelScope.CategoryId.HasValue;
            if (synced) { queryType = PermissionOverwriteScopeType.Category; queryScopeId = channelScope.CategoryId!.Value; }
        }
        var rows = await db.CommunityPermissionOverwrites.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ScopeType == queryType && value.ScopeId == queryScopeId)
            .OrderBy(value => value.TargetType).ThenBy(value => value.TargetId)
            .Select(value => new PermissionOverwriteDto(value.TargetType, value.TargetId, value.Allow, value.Deny))
            .ToListAsync();
        return Results.Ok(new PermissionOverwriteScopeDto(communityId, type, scopeId, synced, rows));
    }

    private static async Task<IResult> SetPermissionOverwriteAsync(Guid communityId, string scopeType, Guid scopeId,
        SetPermissionOverwriteRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime,
        CommunityVoicePermissionEnforcer voicePermissions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!TryScope(scopeType, out var type) || !await ScopeExistsAsync(communityId, type, scopeId, db))
            return Results.NotFound();
        if (!await CanManagePermissionsAsync(communityId, type, scopeId, session.AccountId, db, authorization))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if ((request.Allow & request.Deny) != 0 || (request.Allow | request.Deny) != ((request.Allow | request.Deny) & ChannelPermissions))
            return Invalid("An overwrite contains invalid or conflicting permission values.");
        if (!await ValidateTargetAsync(communityId, request.TargetType, request.TargetId, db))
            return Invalid("The overwrite target is not a role or member of this Server.");
        var actor = type == PermissionOverwriteScopeType.Channel
            ? await authorization.GetChannelAccessAsync(communityId, scopeId, session.AccountId, db)
            : await authorization.GetCategoryAccessAsync(communityId, scopeId, session.AccountId, db);
        if (!actor.IsOwner && !actor.Has(CommunityPermission.Administrator) &&
            (request.Allow & ~actor.Permissions) != 0)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.TargetType == PermissionOverwriteTargetType.Role && request.TargetId is { } roleId &&
            !await authorization.CanManageRoleAsync(communityId, session.AccountId, roleId, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (type == PermissionOverwriteScopeType.Channel)
            await UnsyncChannelAsync(communityId, scopeId, db);
        var row = await db.CommunityPermissionOverwrites.SingleOrDefaultAsync(value => value.CommunityId == communityId &&
            value.ScopeType == type && value.ScopeId == scopeId && value.TargetType == request.TargetType &&
            value.TargetId == request.TargetId);
        if (request.Allow == CommunityPermission.None && request.Deny == CommunityPermission.None)
        {
            if (row is not null) db.CommunityPermissionOverwrites.Remove(row);
        }
        else if (row is null)
            db.CommunityPermissionOverwrites.Add(new CommunityPermissionOverwrite
            {
                Id = Guid.NewGuid(), CommunityId = communityId, ScopeType = type, ScopeId = scopeId,
                TargetType = request.TargetType, TargetId = request.TargetId,
                Allow = request.Allow, Deny = request.Deny, Community = null!
            });
        else { row.Allow = request.Allow; row.Deny = request.Deny; }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await voicePermissions.EnforceAsync(communityId, db);
        await realtime.PublishAsync(communityId, "permissions-updated", db);
        return Results.NoContent();
    }

    private static async Task<IResult> ReplacePermissionOverwritesAsync(Guid communityId, string scopeType, Guid scopeId,
        ReplacePermissionOverwritesRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime,
        CommunityVoicePermissionEnforcer voicePermissions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!TryScope(scopeType, out var type) || !await ScopeExistsAsync(communityId, type, scopeId, db))
            return Results.NotFound();
        if (!await CanManagePermissionsAsync(communityId, type, scopeId, session.AccountId, db, authorization))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.Overwrites.Count > 256) return Invalid("Too many permission overwrite targets.");

        var duplicateTarget = request.Overwrites.GroupBy(value => (value.TargetType, value.TargetId))
            .Any(group => group.Count() > 1);
        if (duplicateTarget) return Invalid("A role or member may only have one overwrite at this scope.");
        var actor = type == PermissionOverwriteScopeType.Channel
            ? await authorization.GetChannelAccessAsync(communityId, scopeId, session.AccountId, db)
            : await authorization.GetCategoryAccessAsync(communityId, scopeId, session.AccountId, db);
        foreach (var row in request.Overwrites.Where(value =>
                     value.TargetType != PermissionOverwriteTargetType.Everyone ||
                     value.Allow != CommunityPermission.None || value.Deny != CommunityPermission.None))
        {
            if ((row.Allow & row.Deny) != 0 || (row.Allow | row.Deny) != ((row.Allow | row.Deny) & ChannelPermissions))
                return Invalid("An overwrite contains invalid or conflicting permission values.");
            if (!await ValidateTargetAsync(communityId, row.TargetType, row.TargetId, db))
                return Invalid("An overwrite target is not a role or member of this Server.");
            if (!actor.IsOwner && !actor.Has(CommunityPermission.Administrator) &&
                (row.Allow & ~actor.Permissions) != 0) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (row.TargetType == PermissionOverwriteTargetType.Role && row.TargetId is { } roleId &&
                !await authorization.CanManageRoleAsync(communityId, session.AccountId, roleId, db))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (type == PermissionOverwriteScopeType.Channel) await UnsyncChannelAsync(communityId, scopeId, db);
        var existing = await db.CommunityPermissionOverwrites.Where(value => value.CommunityId == communityId &&
            value.ScopeType == type && value.ScopeId == scopeId).ToListAsync();
        db.CommunityPermissionOverwrites.RemoveRange(existing);
        foreach (var row in request.Overwrites)
            db.CommunityPermissionOverwrites.Add(new CommunityPermissionOverwrite
            {
                Id = Guid.NewGuid(), CommunityId = communityId, ScopeType = type, ScopeId = scopeId,
                TargetType = row.TargetType, TargetId = row.TargetId, Allow = row.Allow, Deny = row.Deny,
                Community = null!
            });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await voicePermissions.EnforceAsync(communityId, db);
        var revision = await realtime.PublishAsync(communityId, "permissions-updated", db);
        var canonicalRows = await db.CommunityPermissionOverwrites.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ScopeType == type && value.ScopeId == scopeId)
            .OrderBy(value => value.TargetType).ThenBy(value => value.TargetId)
            .Select(value => new PermissionOverwriteDto(value.TargetType, value.TargetId, value.Allow, value.Deny))
            .ToListAsync();
        return Results.Ok(new PermissionOverwriteSaveResultDto(
            new(communityId, type, scopeId, PermissionsSyncedToCategory: false, canonicalRows), revision));
    }

    private static async Task<IResult> RemovePermissionOverwriteAsync(Guid communityId, string scopeType, Guid scopeId,
        RemovePermissionOverwriteRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime,
        CommunityVoicePermissionEnforcer voicePermissions)
    {
        return await SetPermissionOverwriteAsync(communityId, scopeType, scopeId,
            new(request.TargetType, request.TargetId, CommunityPermission.None, CommunityPermission.None),
            context, db, sessions, authorization, realtime, voicePermissions);
    }

    private static async Task<IResult> SyncChannelPermissionsAsync(Guid communityId, Guid channelId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime, CommunityVoicePermissionEnforcer voicePermissions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        if (channel.CategoryId is null) return Invalid("Root channels cannot sync category permissions.");
        if (!await CanManagePermissionsAsync(communityId, PermissionOverwriteScopeType.Channel, channelId,
                session.AccountId, db, authorization)) return Results.Forbid();
        var ownRows = await db.CommunityPermissionOverwrites.Where(value => value.CommunityId == communityId &&
            value.ScopeType == PermissionOverwriteScopeType.Channel && value.ScopeId == channelId).ToListAsync();
        db.CommunityPermissionOverwrites.RemoveRange(ownRows);
        channel.PermissionsSyncedToCategory = true;
        await db.SaveChangesAsync();
        await voicePermissions.EnforceAsync(communityId, db);
        await realtime.PublishAsync(communityId, "permissions-synced", db);
        return Results.NoContent();
    }

    private static async Task UnsyncChannelAsync(Guid communityId, Guid channelId, IridiumDbContext db)
    {
        var channel = await db.CommunityChannels.SingleAsync(value => value.CommunityId == communityId && value.Id == channelId);
        if (!channel.PermissionsSyncedToCategory || channel.CategoryId is null) return;
        var categoryRows = await db.CommunityPermissionOverwrites.AsNoTracking().Where(value =>
            value.CommunityId == communityId && value.ScopeType == PermissionOverwriteScopeType.Category &&
            value.ScopeId == channel.CategoryId).ToListAsync();
        foreach (var source in categoryRows)
            db.CommunityPermissionOverwrites.Add(new CommunityPermissionOverwrite
            {
                Id = Guid.NewGuid(), CommunityId = communityId, ScopeType = PermissionOverwriteScopeType.Channel,
                ScopeId = channelId, TargetType = source.TargetType, TargetId = source.TargetId,
                Allow = source.Allow, Deny = source.Deny, Community = null!
            });
        channel.PermissionsSyncedToCategory = false;
    }

    private static bool TryScope(string value, out PermissionOverwriteScopeType type) =>
        Enum.TryParse(value, true, out type) && Enum.IsDefined(type);
    private static Task<bool> ScopeExistsAsync(Guid communityId, PermissionOverwriteScopeType type, Guid scopeId,
        IridiumDbContext db) => type == PermissionOverwriteScopeType.Channel
        ? db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == scopeId)
        : db.CommunityCategories.AnyAsync(value => value.CommunityId == communityId && value.Id == scopeId);
    private static async Task<bool> CanManagePermissionsAsync(Guid communityId, PermissionOverwriteScopeType type,
        Guid scopeId, Guid accountId, IridiumDbContext db, CommunityAuthorizationService authorization)
    {
        var baseAccess = await authorization.GetAccessAsync(communityId, accountId, db);
        if (baseAccess.IsOwner || baseAccess.Has(CommunityPermission.Administrator)) return true;
        if (type == PermissionOverwriteScopeType.Channel)
        {
            var access = await authorization.GetChannelAccessAsync(communityId, scopeId, accountId, db);
            return access.Has(CommunityPermission.ViewChannels) && access.Has(CommunityPermission.ManagePermissions);
        }
        var categoryAccess = await authorization.GetCategoryAccessAsync(communityId, scopeId, accountId, db);
        return categoryAccess.Has(CommunityPermission.ViewChannels) &&
               categoryAccess.Has(CommunityPermission.ManagePermissions);
    }
    private static async Task<bool> ValidateTargetAsync(Guid communityId, PermissionOverwriteTargetType type,
        Guid? targetId, IridiumDbContext db) => type switch
    {
        PermissionOverwriteTargetType.Everyone => targetId is null,
        PermissionOverwriteTargetType.Role => targetId.HasValue && await db.CommunityRoles.AnyAsync(value =>
            value.CommunityId == communityId && value.Id == targetId && !value.IsDefault),
        PermissionOverwriteTargetType.Member => targetId.HasValue && await db.CommunityMembers.AnyAsync(value =>
            value.CommunityId == communityId && value.AccountId == targetId),
        _ => false
    };

    internal static int Depth(CommunityCategory category, IReadOnlyList<CommunityCategory> categories)
    {
        var depth = 1;
        var parentId = category.ParentCategoryId;
        var visited = new HashSet<Guid> { category.Id };
        while (parentId is { } id)
        {
            if (!visited.Add(id)) return int.MaxValue;
            var parent = categories.SingleOrDefault(value => value.Id == id);
            if (parent is null) break;
            depth++;
            parentId = parent.ParentCategoryId;
        }
        return depth;
    }

    internal static HashSet<Guid> Descendants(Guid categoryId, IReadOnlyList<CommunityCategory> categories)
    {
        var result = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        pending.Enqueue(categoryId);
        while (pending.TryDequeue(out var parent))
            foreach (var child in categories.Where(value => value.ParentCategoryId == parent))
                if (result.Add(child.Id)) pending.Enqueue(child.Id);
        return result;
    }

    private static int SubtreeHeight(Guid categoryId, IReadOnlyList<CommunityCategory> categories)
    {
        var children = categories.Where(value => value.ParentCategoryId == categoryId).ToArray();
        return children.Length == 0 ? 1 : 1 + children.Max(value => SubtreeHeight(value.Id, categories));
    }

    private static SidebarDestination? ResolveDestination(IReadOnlyList<SidebarItem> items, SidebarItem dragged,
        CommunitySidebarMoveRequest request)
    {
        if (request.Intent == CommunitySidebarDropIntent.End)
        {
            if (request.TargetParentCategoryId is { } parent &&
                !items.Any(value => value.Type == CommunitySidebarItemType.Category && value.Id == parent)) return null;
            return new(request.TargetParentCategoryId, int.MaxValue);
        }
        if (request.Intent == CommunitySidebarDropIntent.InsideAtStart)
        {
            if (request.TargetParentCategoryId is not { } parentId ||
                !items.Any(value => value.Type == CommunitySidebarItemType.Category && value.Id == parentId)) return null;
            return new(parentId, 0);
        }
        if (request.TargetItemId is not { } targetId || request.TargetItemType is not { } targetType) return null;
        var target = items.SingleOrDefault(value => value.Id == targetId && value.Type == targetType);
        if (target is null || target.Matches(dragged)) return null;
        if (request.Intent == CommunitySidebarDropIntent.Inside)
            return target.Category is null ? null : new(target.Id, int.MaxValue);
        if (request.Intent is not (CommunitySidebarDropIntent.Before or CommunitySidebarDropIntent.After)) return null;
        var siblings = OrderedSidebarItems(items, target.ParentCategoryId, dragged);
        var targetIndex = siblings.FindIndex(value => value.Matches(target));
        return targetIndex < 0 ? null : new(target.ParentCategoryId,
            targetIndex + (request.Intent == CommunitySidebarDropIntent.After ? 1 : 0));
    }

    internal static void ApplySidebarMove(IReadOnlyList<SidebarItem> items, SidebarItem dragged,
        SidebarDestination destination)
    {
        var sourceParentId = dragged.ParentCategoryId;
        var source = OrderedSidebarItems(items, sourceParentId, dragged);
        var target = sourceParentId == destination.ParentCategoryId
            ? source
            : OrderedSidebarItems(items, destination.ParentCategoryId, dragged);
        dragged.ParentCategoryId = destination.ParentCategoryId;
        target.Insert(Math.Clamp(destination.Position, 0, target.Count), dragged);
        AssignSidebarPositions(source);
        if (!ReferenceEquals(source, target)) AssignSidebarPositions(target);
    }

    private static List<SidebarItem> OrderedSidebarItems(IEnumerable<SidebarItem> items, Guid? parentId,
        SidebarItem excluded) => items.Where(value => value.ParentCategoryId == parentId && !value.Matches(excluded))
        .OrderBy(value => value.Position).ThenBy(value => value.Type).ThenBy(value => value.Id).ToList();

    private static void AssignSidebarPositions(IReadOnlyList<SidebarItem> siblings)
    {
        for (var index = 0; index < siblings.Count; index++) siblings[index].Position = index;
    }

    private static async Task NormalizeSidebarPositionsAsync(Guid communityId, Guid? parentId,
        IridiumDbContext db)
    {
        var categories = await LoadCategoriesAsync(communityId, db);
        var channels = await LoadChannelsAsync(communityId, db);
        AssignSidebarPositions(OrderedSidebarItems(SidebarItems(categories, channels), parentId,
            SidebarItem.None));
    }

    private static Task<List<CommunityCategory>> LoadCategoriesAsync(Guid communityId, IridiumDbContext db) =>
        db.CommunityCategories.Where(value => value.CommunityId == communityId).ToListAsync();

    private static Task<List<CommunityChannel>> LoadChannelsAsync(Guid communityId, IridiumDbContext db) =>
        db.CommunityChannels.Where(value => value.CommunityId == communityId && value.ParentForumChannelId == null).ToListAsync();

    private static List<SidebarItem> SidebarItems(IEnumerable<CommunityCategory> categories,
        IEnumerable<CommunityChannel> channels) => categories.Select(value => new SidebarItem(value))
        .Concat(channels.Select(value => new SidebarItem(value))).ToList();

    private static async Task<IResult?> RequireManagerAsync(Guid communityId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        return await authorization.HasPermissionAsync(communityId, session.AccountId,
            CommunityPermission.ManageChannels, db) ? null : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static Task<bool> CategoryExistsAsync(Guid communityId, Guid categoryId, IridiumDbContext db) =>
        db.CommunityCategories.AnyAsync(value => value.CommunityId == communityId && value.Id == categoryId);

    private static IResult? ValidateName(string name, string subject) => name.Trim().Length is < 1 or > 100
        ? Invalid($"{subject} names must be between 1 and 100 characters.") : null;

    private static IResult Invalid(string message) => Results.BadRequest(new { message });

    private static IResult CategoryMoveConflict() => Results.Conflict(new
    {
        message = "The sidebar order changed while this move was being saved. Reload the Community structure and try again."
    });

    private static bool IsSqliteWriteConflict(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };

    internal sealed class SidebarItem
    {
        public static SidebarItem None { get; } = new();
        private SidebarItem() { }
        public SidebarItem(CommunityCategory category) => Category = category;
        public SidebarItem(CommunityChannel channel) => Channel = channel;
        public CommunityCategory? Category { get; }
        public CommunityChannel? Channel { get; }
        public Guid Id => Category?.Id ?? Channel?.Id ?? Guid.Empty;
        public CommunitySidebarItemType Type => Category is not null
            ? CommunitySidebarItemType.Category : CommunitySidebarItemType.Channel;
        public int Position { get => Category?.Position ?? Channel?.Position ?? 0; set { if (Category is not null) Category.Position = value; else if (Channel is not null) Channel.Position = value; } }
        public Guid? ParentCategoryId { get => Category?.ParentCategoryId ?? Channel?.CategoryId; set { if (Category is not null) Category.ParentCategoryId = value; else if (Channel is not null) Channel.CategoryId = value; } }
        public bool Matches(SidebarItem other) => Id != Guid.Empty && Id == other.Id && Type == other.Type;
    }

    internal sealed record SidebarDestination(Guid? ParentCategoryId, int Position);

    private static string? NormalizeChannelName(string value)
    {
        var name = Regex.Replace(value.Trim().ToLowerInvariant().Replace(' ', '-'), "-+", "-");
        return name.Length is >= 1 and <= 100 && ChannelNamePattern().IsMatch(name) ? name : null;
    }

    private static CommunityCategoryDto ToDto(CommunityCategory value) =>
        new(value.Id, value.CommunityId, value.Name, value.Position, value.ParentCategoryId);
    private static CommunityChannelDto ToDto(CommunityChannel value) =>
        new(value.Id, value.CommunityId, value.CategoryId, value.Name, value.Position, value.CreatedAt,
            Kind: value.Kind, PermissionsSyncedToCategory: value.PermissionsSyncedToCategory,
            RequireTag: value.RequireTag);

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelNamePattern();
}
