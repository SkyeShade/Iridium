using Iridium.Protocol;
using Iridium.Server.Configuration;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Api;

public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/attachments", UploadAsync).DisableAntiforgery();
        endpoints.MapGet("/api/attachments/{attachmentId:guid}", DownloadOriginalAsync);
        endpoints.MapGet("/api/attachments/{attachmentId:guid}/preview", DownloadPreviewAsync);
        endpoints.MapGet("/api/attachments/{attachmentId:guid}/playback-access", PlaybackAccessAsync);
        endpoints.MapGet("/api/attachments/{attachmentId:guid}/playback", PlaybackAsync);
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(HttpContext context, IridiumDbContext db,
        SessionService sessions, IAttachmentStorage storage, IImagePreviewGenerator previewGenerator,
        IAttachmentMediaTypeValidator mediaTypeValidator,
        IOptions<NodeOptions> options,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!context.Request.HasFormContentType) return Results.BadRequest(new { message = "A multipart file is required." });
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Results.BadRequest(new { message = "The selected file is empty." });
        if (file.Length > options.Value.MaxAttachmentBytes)
            return Results.BadRequest(new { message = $"Files may not exceed {options.Value.MaxAttachmentBytes} bytes." });

        var id = Guid.NewGuid();
        var originalObjectKey = Guid.NewGuid().ToString("N");
        var previewObjectKey = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "attachment";
        if (fileName.Length > 255) fileName = fileName[..255];
        string contentType;
        try
        {
            await using var validationStream = file.OpenReadStream();
            contentType = await mediaTypeValidator.ValidateAsync(validationStream, file.ContentType, cancellationToken);
        }
        catch (AttachmentMediaValidationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/octet-stream";
        if (contentType.Length > 255) contentType = contentType[..255];
        var isSpoiler = bool.TryParse(form["isSpoiler"], out var requestedSpoiler) && requestedSpoiler;
        var width = ValidDimension(form["width"].ToString());
        var height = ValidDimension(form["height"].ToString());
        var averageColor = System.Text.RegularExpressions.Regex.IsMatch(form["averageColor"].ToString(), "^#[0-9a-fA-F]{6}$")
            ? form["averageColor"].ToString().ToUpperInvariant() : null;
        GeneratedImagePreview? preview = null;
        try
        {
            await using (var originalStream = file.OpenReadStream())
                await storage.StoreAsync(originalObjectKey, originalStream, cancellationToken);
            await using (var previewSource = file.OpenReadStream())
                preview = await previewGenerator.GenerateAsync(previewSource, contentType, cancellationToken);
            if (preview is not null)
            {
                await using var previewStream = new MemoryStream(preview.Content, writable: false);
                await storage.StoreAsync(previewObjectKey, previewStream, cancellationToken);
            }
        }
        catch
        {
            await storage.DeleteAsync(originalObjectKey, cancellationToken);
            await storage.DeleteAsync(previewObjectKey, cancellationToken);
            throw;
        }

        var attachment = new Attachment
        {
            Id = id, UploaderAccountId = session.AccountId, UploaderAccount = session.Account,
            OriginalFileName = fileName, OriginalObjectKey = originalObjectKey,
            PreviewObjectKey = preview is null ? null : previewObjectKey,
            OriginalContentType = contentType, PreviewContentType = preview?.ContentType,
            OriginalSizeBytes = file.Length, PreviewSizeBytes = preview?.Content.LongLength,
            CreatedAt = DateTimeOffset.UtcNow, IsSpoiler = isSpoiler,
            Width = preview?.OriginalWidth ?? width, Height = preview?.OriginalHeight ?? height,
            AverageColor = preview?.AverageColor ?? averageColor
        };
        db.Attachments.Add(attachment);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(originalObjectKey, cancellationToken);
            await storage.DeleteAsync(previewObjectKey, cancellationToken);
            throw;
        }
        return Results.Ok(new AttachmentUploadDto(id, fileName, contentType, file.Length,
            attachment.Width, attachment.Height, attachment.AverageColor, isSpoiler,
            attachment.PreviewContentType, attachment.PreviewSizeBytes));
    }

    private static Task<IResult> DownloadOriginalAsync(Guid attachmentId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, IAttachmentStorage storage,
        CancellationToken cancellationToken) => DownloadAsync(attachmentId, false, context, db, sessions,
            authorization, storage, cancellationToken);

    private static Task<IResult> DownloadPreviewAsync(Guid attachmentId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization, IAttachmentStorage storage,
        CancellationToken cancellationToken) => DownloadAsync(attachmentId, true, context, db, sessions,
            authorization, storage, cancellationToken);

    private static async Task<IResult> PlaybackAccessAsync(Guid attachmentId, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IAttachmentPlaybackTokenService tokens, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var attachment = await FindAttachmentAsync(attachmentId, db, cancellationToken);
        if (attachment is null) return Results.NotFound();
        if (!await CanAccessAsync(attachment, session.AccountId, authorization, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!attachment.OriginalContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var access = tokens.Issue(attachmentId, session.AccountId);
        var url = $"api/attachments/{attachmentId}/playback?token={Uri.EscapeDataString(access.Token)}";
        return Results.Ok(new AttachmentPlaybackAccessDto(url, access.ExpiresAt));
    }

    private static async Task<IResult> PlaybackAsync(Guid attachmentId, HttpContext context,
        IridiumDbContext db, CommunityAuthorizationService authorization, IAttachmentStorage storage,
        IAttachmentPlaybackTokenService tokens, CancellationToken cancellationToken)
    {
        if (!tokens.TryValidate(attachmentId, context.Request.Query["token"], out var accountId))
            return Results.Unauthorized();
        var attachment = await FindAttachmentAsync(attachmentId, db, cancellationToken);
        if (attachment is null) return Results.NotFound();
        if (!attachment.OriginalContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (!await CanAccessAsync(attachment, accountId, authorization, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var stream = await storage.OpenReadAsync(attachment.OriginalObjectKey, cancellationToken);
        if (stream is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "private,max-age=3600";
        return Results.File(stream, "video/mp4", enableRangeProcessing: true);
    }

    private static async Task<IResult> DownloadAsync(Guid attachmentId, bool preview, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IAttachmentStorage storage, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var attachment = await FindAttachmentAsync(attachmentId, db, cancellationToken);
        if (attachment is null) return Results.NotFound();
        if (!await CanAccessAsync(attachment, session.AccountId, authorization, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var usePreview = preview && attachment.PreviewObjectKey is not null;
        var stream = await storage.OpenReadAsync(
            usePreview ? attachment.PreviewObjectKey! : attachment.OriginalObjectKey, cancellationToken);
        if (stream is not null)
            context.Response.Headers.CacheControl = "private,max-age=31536000,immutable";
        return stream is null
            ? Results.NotFound()
            : Results.File(stream, usePreview ? attachment.PreviewContentType! : attachment.OriginalContentType,
                usePreview ? null : attachment.OriginalFileName, enableRangeProcessing: true);
    }

    private static Task<Attachment?> FindAttachmentAsync(Guid attachmentId, IridiumDbContext db,
        CancellationToken cancellationToken) => db.Attachments.AsNoTracking()
        .Include(value => value.ChannelMessage)
        .Include(value => value.DirectMessage).ThenInclude(value => value!.Conversation)
        .SingleOrDefaultAsync(value => value.Id == attachmentId, cancellationToken);

    private static async Task<bool> CanAccessAsync(Attachment attachment, Guid accountId,
        CommunityAuthorizationService authorization, IridiumDbContext db) =>
        attachment.ChannelMessage is { } channelMessage
            ? await authorization.HasChannelPermissionAsync(channelMessage.CommunityId, channelMessage.ChannelId,
                accountId, CommunityPermission.ViewChannels, db)
            : attachment.DirectMessage?.Conversation is { } conversation
                ? conversation.ParticipantAAccountId == accountId || conversation.ParticipantBAccountId == accountId
                : attachment.UploaderAccountId == accountId;

    private static int? ValidDimension(string value) =>
        int.TryParse(value, out var dimension) && dimension is > 0 and <= 100_000 ? dimension : null;
}
