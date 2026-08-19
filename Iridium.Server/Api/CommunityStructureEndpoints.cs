using System.Text.RegularExpressions;
using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static partial class CommunityStructureEndpoints
{
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

    private static async Task<IResult> GetStructureAsync(
        Guid communityId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.Forbid();

        var categories = await db.CommunityCategories.Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.Position).ThenBy(value => value.Name)
            .Select(value => new CommunityCategoryDto(value.Id, value.CommunityId, value.Name, value.Position, value.ParentCategoryId))
            .ToListAsync();
        var channels = await db.CommunityChannels.Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.CategoryId).ThenBy(value => value.Position).ThenBy(value => value.Name)
            .Select(value => new CommunityChannelDto(value.Id, value.CommunityId, value.CategoryId, value.Name, value.Position, value.CreatedAt))
            .ToListAsync();
        return Results.Ok(new CommunityStructureDto(
            communityId,
            access.Has(CommunityPermission.ManageChannels),
            categories,
            channels,
            access.Permissions,
            access.IsOwner));
    }

    private static async Task<IResult> CreateCategoryAsync(
        Guid communityId, CreateCategoryRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var nameError = ValidateName(request.Name, "Category");
        if (nameError is not null) return nameError;
        if (!await db.Communities.AnyAsync(value => value.Id == communityId)) return Results.NotFound();

        var category = new CommunityCategory
        {
            Id = Guid.NewGuid(), CommunityId = communityId, Name = request.Name.Trim(),
            Position = await TopLevelCountAsync(communityId, db),
            Community = null!
        };
        db.CommunityCategories.Add(category);
        await db.SaveChangesAsync();
        return Results.Created($"/api/communities/{communityId}/categories/{category.Id}", ToDto(category));
    }

    private static async Task<IResult> UpdateCategoryAsync(
        Guid communityId, Guid categoryId, UpdateCategoryRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var nameError = ValidateName(request.Name, "Category");
        if (nameError is not null) return nameError;
        var category = await db.CommunityCategories.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == categoryId);
        if (category is null) return Results.NotFound();
        category.Name = request.Name.Trim();
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(category));
    }

    private static async Task<IResult> MoveCategoryAsync(
        Guid communityId, Guid categoryId, MoveCategoryRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var topLevel = await LoadTopLevelAsync(communityId, db);
        var item = topLevel.SingleOrDefault(value => value.Category?.Id == categoryId);
        if (item is null) return Results.NotFound();
        topLevel.Remove(item);
        topLevel.Insert(Math.Clamp(request.Position, 0, topLevel.Count), item);
        ApplyTopLevelPositions(topLevel);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteCategoryAsync(
        Guid communityId, Guid categoryId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var topLevel = await LoadTopLevelAsync(communityId, db);
        var categoryItem = topLevel.SingleOrDefault(value => value.Category?.Id == categoryId);
        if (categoryItem?.Category is not { } category) return Results.NotFound();
        var insertionIndex = topLevel.IndexOf(categoryItem);
        topLevel.Remove(categoryItem);
        var contained = await db.CommunityChannels
            .Where(value => value.CommunityId == communityId && value.CategoryId == categoryId)
            .OrderBy(value => value.Position).ToListAsync();
        foreach (var channel in contained)
            channel.CategoryId = null;
        topLevel.InsertRange(insertionIndex, contained.Select(channel => new TopLevelItem(null, channel)));
        ApplyTopLevelPositions(topLevel);
        db.CommunityCategories.Remove(category);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> CreateChannelAsync(
        Guid communityId, CreateChannelRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var name = NormalizeChannelName(request.Name);
        if (name is null) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = ["Channel names must be 1-100 characters using letters, numbers, underscores, or hyphens."] });
        if (!await CategoryExistsAsync(communityId, request.CategoryId, db)) return Results.BadRequest(new { message = "The selected category does not belong to this Community." });

        var position = request.CategoryId is null
            ? await TopLevelCountAsync(communityId, db)
            : await db.CommunityChannels.Where(value => value.CommunityId == communityId && value.CategoryId == request.CategoryId)
                .CountAsync();
        var channel = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = communityId, CategoryId = request.CategoryId, Name = name,
            Position = position + 1, CreatedAt = DateTimeOffset.UtcNow, Community = null!
        };
        db.CommunityChannels.Add(channel);
        await db.SaveChangesAsync();
        return Results.Created($"/api/communities/{communityId}/channels/{channel.Id}", ToDto(channel));
    }

    private static async Task<IResult> UpdateChannelAsync(
        Guid communityId, Guid channelId, UpdateChannelRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var name = NormalizeChannelName(request.Name);
        if (name is null) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = ["Enter a valid channel name."] });
        if (!await CategoryExistsAsync(communityId, request.CategoryId, db)) return Results.BadRequest(new { message = "The selected category does not belong to this Community." });
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        channel.Name = name;
        if (channel.CategoryId != request.CategoryId) await MoveChannelCoreAsync(channel, request.CategoryId, int.MaxValue, db);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(channel));
    }

    private static async Task<IResult> MoveChannelAsync(
        Guid communityId, Guid channelId, MoveChannelRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        if (!await CategoryExistsAsync(communityId, request.CategoryId, db)) return Results.BadRequest(new { message = "The selected category does not belong to this Community." });
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        await MoveChannelCoreAsync(channel, request.CategoryId, request.Position, db);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteChannelAsync(
        Guid communityId, Guid channelId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var denied = await RequireManagerAsync(communityId, context, db, sessions, authorization);
        if (denied is not null) return denied;
        var channel = await db.CommunityChannels.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == channelId);
        if (channel is null) return Results.NotFound();
        if (channel.CategoryId is null)
        {
            var topLevel = await LoadTopLevelAsync(communityId, db, excludeChannelId: channel.Id);
            ApplyTopLevelPositions(topLevel);
        }
        else
        {
            await ReindexChannelsAsync(communityId, channel.CategoryId, db, channel.Id);
        }
        db.CommunityChannels.Remove(channel);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task MoveChannelCoreAsync(CommunityChannel channel, Guid? categoryId, int position, IridiumDbContext db)
    {
        var oldCategory = channel.CategoryId;
        channel.CategoryId = categoryId;
        if (categoryId is null)
        {
            var topLevel = await LoadTopLevelAsync(channel.CommunityId, db, excludeChannelId: channel.Id);
            topLevel.Insert(Math.Clamp(position, 0, topLevel.Count), new TopLevelItem(null, channel));
            ApplyTopLevelPositions(topLevel);
        }
        else
        {
            var target = await db.CommunityChannels
                .Where(value => value.CommunityId == channel.CommunityId && value.CategoryId == categoryId && value.Id != channel.Id)
                .OrderBy(value => value.Position).ToListAsync();
            target.Insert(Math.Clamp(position, 0, target.Count), channel);
            for (var index = 0; index < target.Count; index++) target[index].Position = index;
        }
        if (oldCategory == categoryId) return;
        if (oldCategory is null)
        {
            var oldTopLevel = await LoadTopLevelAsync(channel.CommunityId, db, excludeChannelId: channel.Id);
            ApplyTopLevelPositions(oldTopLevel);
        }
        else
        {
            await ReindexChannelsAsync(channel.CommunityId, oldCategory, db, channel.Id);
        }
    }

    private static async Task<int> TopLevelCountAsync(Guid communityId, IridiumDbContext db) =>
        await db.CommunityCategories.CountAsync(value => value.CommunityId == communityId) +
        await db.CommunityChannels.CountAsync(value => value.CommunityId == communityId && value.CategoryId == null);

    private static async Task<List<TopLevelItem>> LoadTopLevelAsync(
        Guid communityId, IridiumDbContext db, Guid? excludeChannelId = null)
    {
        var categories = await db.CommunityCategories.Where(value => value.CommunityId == communityId).ToListAsync();
        var channels = await db.CommunityChannels.Where(value => value.CommunityId == communityId &&
            value.CategoryId == null && value.Id != excludeChannelId).ToListAsync();
        return categories.Select(category => new TopLevelItem(category, null))
            .Concat(channels.Select(channel => new TopLevelItem(null, channel)))
            .OrderBy(value => value.Position)
            .ThenBy(value => value.Category is null ? 0 : 1)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyTopLevelPositions(IReadOnlyList<TopLevelItem> items)
    {
        for (var index = 0; index < items.Count; index++) items[index].SetPosition(index);
    }

    private static async Task ReindexChannelsAsync(Guid communityId, Guid? categoryId, IridiumDbContext db, Guid? exclude = null)
    {
        var channels = await db.CommunityChannels
            .Where(value => value.CommunityId == communityId && value.CategoryId == categoryId && value.Id != exclude)
            .OrderBy(value => value.Position).ToListAsync();
        for (var index = 0; index < channels.Count; index++) channels[index].Position = index;
    }

    private static async Task<IResult?> RequireManagerAsync(
        Guid communityId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        return await authorization.HasPermissionAsync(
            communityId, session.AccountId, CommunityPermission.ManageChannels, db)
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static Task<bool> CategoryExistsAsync(Guid communityId, Guid? categoryId, IridiumDbContext db) =>
        categoryId is null ? Task.FromResult(true) : db.CommunityCategories.AnyAsync(value => value.CommunityId == communityId && value.Id == categoryId);

    private static IResult? ValidateName(string name, string subject) => name.Trim().Length is < 1 or > 100
        ? Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(name)] = [$"{subject} names must be between 1 and 100 characters."] })
        : null;

    private static string? NormalizeChannelName(string value)
    {
        var name = Regex.Replace(value.Trim().ToLowerInvariant().Replace(' ', '-'), "-+", "-");
        return name.Length is >= 1 and <= 100 && ChannelNamePattern().IsMatch(name) ? name : null;
    }

    private static CommunityCategoryDto ToDto(CommunityCategory value) => new(value.Id, value.CommunityId, value.Name, value.Position, value.ParentCategoryId);
    private static CommunityChannelDto ToDto(CommunityChannel value) => new(value.Id, value.CommunityId, value.CategoryId, value.Name, value.Position, value.CreatedAt);

    private sealed record TopLevelItem(CommunityCategory? Category, CommunityChannel? Channel)
    {
        public int Position => Category?.Position ?? Channel!.Position;
        public string Name => Category?.Name ?? Channel!.Name;
        public void SetPosition(int position)
        {
            if (Category is not null) Category.Position = position;
            else Channel!.Position = position;
        }
    }

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelNamePattern();
}
