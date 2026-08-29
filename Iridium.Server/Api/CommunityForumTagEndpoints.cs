using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class CommunityForumTagEndpoints
{
    public static IEndpointRouteBuilder MapCommunityForumTagEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}/forums/{channelId:guid}");
        group.MapGet("/tags", ListAsync);
        group.MapPost("/tags", CreateAsync);
        group.MapPut("/tags/order", ReorderAsync);
        group.MapPut("/tags/{tagId:guid}", UpdateAsync);
        group.MapDelete("/tags/{tagId:guid}", DeleteAsync);
        group.MapPut("/posts/{postId:guid}/tags", UpdatePostTagsAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid communityId, Guid channelId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var access = await AccessAsync(communityId, channelId, context, db, sessions, authorization);
        if (access.Result is not null) return access.Result;
        var tags = await LoadAsync(channelId, db);
        return Results.Ok(tags.Select(CommunityForumEndpoints.ToDto).ToArray());
    }

    private static async Task<IResult> CreateAsync(Guid communityId, Guid channelId,
        CreateCommunityForumTagRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var access = await AccessAsync(communityId, channelId, context, db, sessions, authorization, manage: true);
        if (access.Result is not null) return access.Result;
        if (await db.CommunityForumTags.CountAsync(value => value.ChannelId == channelId) >=
            CommunityForumTagLimits.MaximumTagsPerForum)
            return Invalid($"A Forum may define at most {CommunityForumTagLimits.MaximumTagsPerForum} tags.");
        var validation = await ValidateDefinitionAsync(communityId, channelId, request.Name, request.EmojiKind,
            request.StandardEmoji, request.CustomEmojiId, null, access.AccountId!.Value,
            access.Access!.Has(CommunityPermission.UseExternalEmoji), db, authorization);
        if (validation.Error is not null) return Invalid(validation.Error);
        var tag = new CommunityForumTag
        {
            Id = Guid.NewGuid(), CommunityId = communityId, ChannelId = channelId, Name = validation.Name!,
            EmojiKind = request.EmojiKind, StandardEmoji = validation.StandardEmoji,
            CustomEmojiId = validation.CustomEmoji?.Id, CustomEmoji = validation.CustomEmoji,
            Moderated = request.Moderated,
            SortOrder = await db.CommunityForumTags.CountAsync(value => value.ChannelId == channelId),
            CreatedAt = DateTimeOffset.UtcNow, Channel = null!
        };
        db.CommunityForumTags.Add(tag);
        await db.SaveChangesAsync();
        await PublishDefinitionsAsync(communityId, channelId, db, authorization, hub);
        return Results.Created($"/api/communities/{communityId}/forums/{channelId}/tags/{tag.Id}",
            CommunityForumEndpoints.ToDto(tag));
    }

    private static async Task<IResult> UpdateAsync(Guid communityId, Guid channelId, Guid tagId,
        UpdateCommunityForumTagRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var access = await AccessAsync(communityId, channelId, context, db, sessions, authorization, manage: true);
        if (access.Result is not null) return access.Result;
        var tag = await db.CommunityForumTags.Include(value => value.CustomEmoji).SingleOrDefaultAsync(value =>
            value.Id == tagId && value.ChannelId == channelId && value.CommunityId == communityId);
        if (tag is null) return Results.NotFound();
        var validation = await ValidateDefinitionAsync(communityId, channelId, request.Name, request.EmojiKind,
            request.StandardEmoji, request.CustomEmojiId, tagId, access.AccountId!.Value,
            access.Access!.Has(CommunityPermission.UseExternalEmoji), db, authorization);
        if (validation.Error is not null) return Invalid(validation.Error);
        tag.Name = validation.Name!;
        tag.EmojiKind = request.EmojiKind;
        tag.StandardEmoji = validation.StandardEmoji;
        tag.CustomEmojiId = validation.CustomEmoji?.Id;
        tag.CustomEmoji = validation.CustomEmoji;
        tag.Moderated = request.Moderated;
        if (request.SortOrder.HasValue) tag.SortOrder = Math.Max(0, request.SortOrder.Value);
        await db.SaveChangesAsync();
        await NormalizeOrderAsync(channelId, db);
        await PublishDefinitionsAsync(communityId, channelId, db, authorization, hub);
        return Results.Ok(CommunityForumEndpoints.ToDto(tag));
    }

    private static async Task<IResult> ReorderAsync(Guid communityId, Guid channelId,
        ReorderCommunityForumTagsRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var access = await AccessAsync(communityId, channelId, context, db, sessions, authorization, manage: true);
        if (access.Result is not null) return access.Result;
        var tags = await db.CommunityForumTags.Where(value => value.ChannelId == channelId).ToListAsync();
        if (request.TagIds.Count != tags.Count || request.TagIds.Distinct().Count() != tags.Count ||
            request.TagIds.Any(id => tags.All(tag => tag.Id != id)))
            return Invalid("The tag order must contain every tag in this Forum exactly once.");
        for (var index = 0; index < request.TagIds.Count; index++)
            tags.Single(value => value.Id == request.TagIds[index]).SortOrder = index;
        await db.SaveChangesAsync();
        await PublishDefinitionsAsync(communityId, channelId, db, authorization, hub);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid communityId, Guid channelId, Guid tagId,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var access = await AccessAsync(communityId, channelId, context, db, sessions, authorization, manage: true);
        if (access.Result is not null) return access.Result;
        var tag = await db.CommunityForumTags.SingleOrDefaultAsync(value => value.Id == tagId &&
            value.ChannelId == channelId && value.CommunityId == communityId);
        if (tag is null) return Results.NotFound();
        db.CommunityForumTags.Remove(tag);
        await db.SaveChangesAsync();
        await NormalizeOrderAsync(channelId, db);
        await PublishDefinitionsAsync(communityId, channelId, db, authorization, hub);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdatePostTagsAsync(Guid communityId, Guid channelId, Guid postId,
        UpdateCommunityForumPostTagsRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var post = await db.CommunityForumPosts.Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage).Include(value => value.TagAssignments).ThenInclude(value => value.Tag)
            .SingleOrDefaultAsync(value => value.Id == postId);
        if (post is null || post.CommunityId != communityId || post.ForumChannelId != channelId)
            return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(post.CommunityId, post.ForumChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        var moderates = access.Has(CommunityPermission.ManageMessages);
        if (post.AuthorAccountId != session.AccountId && !moderates) return Forbidden();

        var requested = request.TagIds.ToArray();
        var selection = await CommunityForumEndpoints.ValidateTagSelectionAsync(post.ForumChannelId, requested,
            access, db, requireAtLeastOne: false);
        if (selection.Error is not null) return Invalid(selection.Error);
        var preserved = moderates ? [] : post.TagAssignments.Where(value => value.Tag.Moderated)
            .Select(value => value.Tag).ToArray();
        var final = selection.Tags.Concat(preserved).DistinctBy(value => value.Id).ToArray();
        if (final.Length > CommunityForumTagLimits.MaximumTagsPerPost)
            return Invalid($"A Post may have at most {CommunityForumTagLimits.MaximumTagsPerPost} tags.");
        var required = await db.CommunityChannels.Where(value => value.CommunityId == post.CommunityId &&
            value.Id == post.ForumChannelId).Select(value => value.RequireTag).SingleAsync();
        if (required && final.Length == 0) return Invalid("This Forum requires at least one tag.");

        db.CommunityForumPostTags.RemoveRange(post.TagAssignments.Where(value =>
            final.All(tag => tag.Id != value.TagId)));
        foreach (var tag in final.Where(tag => post.TagAssignments.All(value => value.TagId != tag.Id)))
            db.CommunityForumPostTags.Add(new() { Post = post, PostId = post.Id, Tag = tag, TagId = tag.Id });
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dto = await CommunityForumEndpoints.ToDtoAsync(post, db);
        await CommunityForumEndpoints.PublishAsync(communityId, channelId,
            new(communityId, channelId, dto, post.Id, "tags-updated", session.AccountId), db, authorization, hub);
        return Results.Ok(dto);
    }

    private static async Task<(string? Name, string? StandardEmoji, CommunityEmoji? CustomEmoji, string? Error)>
        ValidateDefinitionAsync(Guid communityId, Guid channelId, string input, ReactionEmojiKind? kind,
            string? standardEmoji, Guid? customEmojiId, Guid? existingId, Guid accountId, bool allowExternal,
            IridiumDbContext db, CommunityAuthorizationService authorization)
    {
        var name = input.Trim();
        if (name.Length is < 1 or > CommunityForumTagLimits.MaximumNameLength)
            return (null, null, null, $"Tag names must contain 1 to {CommunityForumTagLimits.MaximumNameLength} characters.");
        if (await db.CommunityForumTags.AnyAsync(value => value.ChannelId == channelId && value.Id != existingId &&
            value.Name.ToLower() == name.ToLower())) return (null, null, null, "Tag names must be unique in this Forum.");
        if (kind is null)
        {
            if (!string.IsNullOrEmpty(standardEmoji) || customEmojiId.HasValue)
                return (null, null, null, "Choose one valid tag emoji.");
            return (name, null, null, null);
        }
        if (kind == ReactionEmojiKind.Standard)
        {
            if (customEmojiId.HasValue || string.IsNullOrEmpty(standardEmoji) ||
                StandardEmojiCatalog.All.All(value => value.Glyph != standardEmoji))
                return (null, null, null, "Choose a valid standard emoji.");
            return (name, standardEmoji, null, null);
        }
        if (kind != ReactionEmojiKind.Custom || !customEmojiId.HasValue || !string.IsNullOrEmpty(standardEmoji))
            return (null, null, null, "Choose a valid Server emoji.");
        var custom = await db.CommunityEmojis.SingleOrDefaultAsync(value => value.Id == customEmojiId);
        if (custom is null || !await authorization.IsMemberAsync(custom.CommunityId, accountId, db))
            return (null, null, null, "That custom emoji is not available to your account.");
        if (custom.CommunityId != communityId && !allowExternal)
            return (null, null, null, "You do not have permission to use custom emoji from another Server.");
        return (name, null, custom, null);
    }

    private static async Task<(CommunityAccessDto? Access, Guid? AccountId, IResult? Result)> AccessAsync(Guid communityId,
        Guid channelId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, bool manage = false)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return (null, null, Results.Unauthorized());
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId &&
            value.Kind == CommunityChannelKind.Forum && value.ParentForumChannelId == null))
            return (null, null, Results.NotFound());
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return (null, session.AccountId, Results.NotFound());
        if (manage && !access.Has(CommunityPermission.ManageMessages)) return (null, session.AccountId, Forbidden());
        return (access, session.AccountId, null);
    }

    private static async Task<List<CommunityForumTag>> LoadAsync(Guid channelId, IridiumDbContext db) =>
        await db.CommunityForumTags.AsNoTracking().Include(value => value.CustomEmoji)
            .Where(value => value.ChannelId == channelId).OrderBy(value => value.SortOrder)
            .ThenBy(value => value.Name).ToListAsync();

    private static async Task NormalizeOrderAsync(Guid channelId, IridiumDbContext db)
    {
        var tags = await db.CommunityForumTags.Where(value => value.ChannelId == channelId)
            .OrderBy(value => value.SortOrder).ThenBy(value => value.Name).ToListAsync();
        for (var index = 0; index < tags.Count; index++) tags[index].SortOrder = index;
        await db.SaveChangesAsync();
    }

    private static async Task PublishDefinitionsAsync(Guid communityId, Guid channelId, IridiumDbContext db,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var dto = (await LoadAsync(channelId, db)).Select(CommunityForumEndpoints.ToDto).ToArray();
        var accounts = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == communityId)
            .Select(value => value.AccountId).ToListAsync();
        var owner = await db.Communities.AsNoTracking().Where(value => value.Id == communityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync();
        if (owner.HasValue) accounts.Add(owner.Value);
        foreach (var accountId in accounts.Distinct())
            if (await authorization.HasChannelPermissionAsync(communityId, channelId, accountId,
                    CommunityPermission.ViewChannels, db))
                await hub.Clients.Group(ChatHub.AccountGroup(accountId)).SendAsync(
                    CommunityForumHubContract.TagsChanged,
                    new CommunityForumTagsChangedEvent(communityId, channelId, dto));
    }

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]>
        { ["forumTags"] = [message] });
    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);
}
