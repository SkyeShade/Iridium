using Iridium.Protocol;
using Iridium.Server.Communities;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class CommunityEmojiEndpoints
{
    public static IEndpointRouteBuilder MapCommunityEmojiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}/emojis");
        group.MapGet("/", ListAsync);
        group.MapGet("/{emojiId:guid}/media", DownloadAsync);
        group.MapPost("/", UploadAsync).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(CommunityEmojiLimits.MaximumMultipartBytes))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = CommunityEmojiLimits.MaximumMultipartBytes });
        group.MapPatch("/{emojiId:guid}", RenameAsync);
        group.MapDelete("/{emojiId:guid}", DeleteAsync);
        endpoints.MapGet("/api/emojis/{emojiId:guid}", ResolveReferenceAsync);
        endpoints.MapGet("/api/emojis/{emojiId:guid}/media", DownloadReferenceAsync);
        return endpoints;
    }

    private static async Task<IResult> DownloadReferenceAsync(Guid emojiId, HttpContext context,
        IridiumDbContext db, SessionService sessions, IAttachmentStorage storage,
        CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(context, db) is null) return Results.Unauthorized();
        var emoji = await db.CommunityEmojis.AsNoTracking().SingleOrDefaultAsync(value => value.Id == emojiId,
            cancellationToken);
        if (emoji is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(emoji.ObjectKey, cancellationToken);
        if (stream is not null) context.Response.Headers.CacheControl = context.Request.Query.ContainsKey("rev")
            ? "private,max-age=31536000,immutable" : "private,no-cache";
        return stream is null ? Results.NotFound() : Results.File(stream, emoji.ContentType,
            enableRangeProcessing: true);
    }

    private static async Task<(AccountSession? Session, IResult? Error)> MemberAsync(Guid communityId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return (null, Results.Unauthorized());
        return await authorization.IsMemberAsync(communityId, session.AccountId, db)
            ? (session, null) : (session, Results.StatusCode(StatusCodes.Status404NotFound));
    }

    private static async Task<(AccountSession? Session, IResult? Error)> ManagerAsync(Guid communityId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var result = await MemberAsync(communityId, context, db, sessions, authorization);
        if (result.Error is not null) return result;
        return await authorization.HasPermissionAsync(communityId, result.Session!.AccountId,
            CommunityPermission.ManageExpressions, db)
            ? result : (result.Session, Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private static async Task<IResult> ListAsync(Guid communityId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, CancellationToken cancellationToken)
    {
        var access = await MemberAsync(communityId, context, db, sessions, authorization);
        if (access.Error is not null) return access.Error;
        var values = await db.CommunityEmojis.AsNoTracking().Where(value => value.CommunityId == communityId)
            .OrderBy(value => value.Name).ToArrayAsync(cancellationToken);
        return Results.Ok(values.Select(ToDto).ToArray());
    }

    private static async Task<IResult> UploadAsync(Guid communityId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, IAttachmentStorage storage,
        IAvatarImageValidator validator, CommunityRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var access = await ManagerAsync(communityId, context, db, sessions, authorization);
        if (access.Error is not null) return access.Error;
        if (!context.Request.HasFormContentType) return Results.BadRequest(new { message = "Choose an emoji image." });
        IFormCollection form;
        try { form = await context.Request.ReadFormAsync(cancellationToken); }
        catch (Exception exception) when (exception is BadHttpRequestException or InvalidDataException)
        { return Results.BadRequest(new { message = $"This emoji exceeds the maximum size of {FileSizeDisplay.Megabytes(CommunityEmojiLimits.MaximumUploadBytes)}." }); }
        var file = form.Files.GetFile("file");
        if (file is null) return Results.BadRequest(new { message = "Choose an emoji image." });
        if (file.Length > CommunityEmojiLimits.MaximumUploadBytes)
            return Results.BadRequest(new { message = $"This emoji is {FileSizeDisplay.Megabytes(file.Length)}. The maximum size is {FileSizeDisplay.Megabytes(CommunityEmojiLimits.MaximumUploadBytes)}." });
        var name = CommunityEmojiNames.Normalize(form["name"].FirstOrDefault() ?? file.FileName);
        if (!CommunityEmojiNames.IsValid(name)) return Results.BadRequest(new { message = "Emoji names must contain 2-32 lowercase letters, numbers, or underscores." });
        if (await db.CommunityEmojis.AnyAsync(value => value.CommunityId == communityId && value.Name == name, cancellationToken))
            return Results.Conflict(new { message = $":{name}: already exists in this Server." });
        if (await db.CommunityEmojis.CountAsync(value => value.CommunityId == communityId, cancellationToken) >= CommunityEmojiLimits.MaximumPerCommunity)
            return Results.Conflict(new { message = $"A Community may store at most {CommunityEmojiLimits.MaximumPerCommunity} custom emojis." });
        ValidatedAvatarImage image;
        try { await using var input = file.OpenReadStream(); image = CommunityEmojiProcessor.Process(await validator.ValidateAsync(input, file.ContentType, cancellationToken)); }
        catch (AvatarImageValidationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        var key = Guid.NewGuid().ToString("N");
        await using (var source = new MemoryStream(image.Content, false)) await storage.StoreAsync(key, source, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var emoji = new CommunityEmoji { Id = Guid.NewGuid(), CommunityId = communityId,
            Community = await db.Communities.SingleAsync(value => value.Id == communityId, cancellationToken), Name = name,
            ObjectKey = key, ContentType = image.ContentType, IsAnimated = image.Animated, Width = image.Width,
            Height = image.Height, SizeBytes = image.Content.LongLength, Revision = now.UtcTicks, CreatedAt = now,
            CreatedByAccountId = access.Session!.AccountId };
        db.CommunityEmojis.Add(emoji);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { await storage.DeleteAsync(key, cancellationToken); throw; }
        await realtime.PublishAsync(communityId, "expressions-updated", db, cancellationToken);
        return Results.Ok(ToDto(emoji));
    }

    private static async Task<IResult> RenameAsync(Guid communityId, Guid emojiId, RenameCommunityEmojiRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var access = await ManagerAsync(communityId, context, db, sessions, authorization);
        if (access.Error is not null) return access.Error;
        var emoji = await db.CommunityEmojis.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == emojiId, cancellationToken);
        if (emoji is null) return Results.NotFound();
        var name = CommunityEmojiNames.Normalize(request.Name);
        if (!CommunityEmojiNames.IsValid(name)) return Results.BadRequest(new { message = "Emoji names must contain 2-32 lowercase letters, numbers, or underscores." });
        if (await db.CommunityEmojis.AnyAsync(value => value.CommunityId == communityId && value.Id != emojiId && value.Name == name, cancellationToken))
            return Results.Conflict(new { message = $":{name}: already exists in this Server." });
        emoji.Name = name; emoji.Revision = Math.Max(emoji.Revision + 1, DateTimeOffset.UtcNow.UtcTicks);
        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishAsync(communityId, "expressions-updated", db, cancellationToken);
        return Results.Ok(ToDto(emoji));
    }

    private static async Task<IResult> DeleteAsync(Guid communityId, Guid emojiId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IAttachmentStorage storage, CommunityRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var access = await ManagerAsync(communityId, context, db, sessions, authorization);
        if (access.Error is not null) return access.Error;
        var emoji = await db.CommunityEmojis.SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == emojiId, cancellationToken);
        if (emoji is null) return Results.NotFound();
        db.CommunityEmojis.Remove(emoji); await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(emoji.ObjectKey, cancellationToken);
        await realtime.PublishAsync(communityId, "expressions-updated", db, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadAsync(Guid communityId, Guid emojiId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IAttachmentStorage storage, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(context, db) is null) return Results.Unauthorized();
        var emoji = await db.CommunityEmojis.AsNoTracking().SingleOrDefaultAsync(value => value.CommunityId == communityId && value.Id == emojiId, cancellationToken);
        if (emoji is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(emoji.ObjectKey, cancellationToken);
        if (stream is not null)
            context.Response.Headers.CacheControl = context.Request.Query.ContainsKey("rev")
                ? "private,max-age=31536000,immutable"
                : "private,no-cache";
        return stream is null ? Results.NotFound() : Results.File(stream, emoji.ContentType, enableRangeProcessing: true);
    }

    private static async Task<IResult> ResolveReferenceAsync(Guid emojiId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(context, db) is null) return Results.Unauthorized();
        var emoji = await db.CommunityEmojis.AsNoTracking().SingleOrDefaultAsync(value => value.Id == emojiId,
            cancellationToken);
        return emoji is null ? Results.NotFound() : Results.Ok(ToDto(emoji));
    }

    private static CommunityEmojiDto ToDto(CommunityEmoji value) => new(value.Id, value.CommunityId, value.Name,
        value.ContentType, value.IsAnimated, value.Width, value.Height, value.SizeBytes, value.Revision,
        value.CreatedAt, value.CreatedByAccountId);
}
