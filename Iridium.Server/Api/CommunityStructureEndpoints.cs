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
        return endpoints;
    }

    private static async Task<IResult> GetStructureAsync(Guid communityId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.Forbid();

        var categories = await db.CommunityCategories.AsNoTracking().Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.ParentCategoryId).ThenBy(value => value.Position).ThenBy(value => value.Name)
            .Select(value => ToDto(value)).ToListAsync();
        var channels = await db.CommunityChannels.AsNoTracking().Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.CategoryId).ThenBy(value => value.Position).ThenBy(value => value.Name)
            .Select(value => ToDto(value)).ToListAsync();
        var readStates = await db.CommunityChannelReadStates.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.AccountId == session.AccountId)
            .ToDictionaryAsync(value => value.ChannelId, value => value.LastReadAt);
        for (var index = 0; index < channels.Count; index++)
        {
            var channel = channels[index];
            readStates.TryGetValue(channel.Id, out var lastReadAt);
            var unread = await db.ChannelMessages.CountAsync(value => value.CommunityId == communityId &&
                value.ChannelId == channel.Id && value.AuthorAccountId != session.AccountId && value.CreatedAt > lastReadAt);
            var mentions = await db.CommunityMentionNotifications.CountAsync(value => value.AccountId == session.AccountId &&
                value.CommunityId == communityId && value.ChannelId == channel.Id && value.ReadAt == null);
            channels[index] = channel with { UnreadCount = unread, MentionCount = mentions };
        }
        return Results.Ok(new CommunityStructureDto(communityId, access.Has(CommunityPermission.ManageChannels),
            categories, channels, access.Permissions, access.IsOwner));
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
            if (parent is null) return Invalid("The parent Category does not belong to this Community.");
            if (Depth(parent, categories) >= MaximumCategoryDepth)
                return Invalid($"Categories may be nested at most {MaximumCategoryDepth} levels deep.");
        }
        var category = new CommunityCategory
        {
            Id = Guid.NewGuid(), CommunityId = communityId, Name = request.Name.Trim(),
            ParentCategoryId = request.ParentCategoryId,
            Position = categories.Count(value => value.ParentCategoryId == request.ParentCategoryId) +
                       await db.CommunityChannels.CountAsync(value => value.CommunityId == communityId &&
                           value.CategoryId == request.ParentCategoryId), Community = null!
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
                if (parent is null) return Invalid("The destination Category does not belong to this Community.");
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
            if (category is null) return Invalid("The selected Category does not belong to this Community.");
        }
        var channel = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = communityId, CategoryId = request.CategoryId, Category = category,
            Name = name, Kind = request.Kind,
            Position = await db.CommunityChannels.CountAsync(value => value.CommunityId == communityId && value.CategoryId == request.CategoryId) +
                       await db.CommunityCategories.CountAsync(value => value.CommunityId == communityId && value.ParentCategoryId == request.CategoryId),
            CreatedAt = DateTimeOffset.UtcNow, Community = null!
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
            return Invalid("The selected Category does not belong to this Community.");
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        channel.Name = name;
        channel.Kind = request.Kind;
        if (channel.CategoryId != request.CategoryId)
        {
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
        db.CommunityChannels.Remove(channel);
        await db.SaveChangesAsync();
        await NormalizeSidebarPositionsAsync(communityId, categoryId, db);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await realtime.PublishAsync(communityId, "channel-deleted", db);
        return Results.NoContent();
    }

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
        db.CommunityChannels.Where(value => value.CommunityId == communityId).ToListAsync();

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
            Kind: value.Kind);

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelNamePattern();
}
