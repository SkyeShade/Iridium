using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Profiles;
using Iridium.Server.Security;
using Iridium.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class BannerPresetEndpoints
{
    public static IEndpointRouteBuilder MapBannerPresetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var presets = endpoints.MapGroup("/api/account/banner-presets");
        presets.MapGet("/", ListAsync);
        presets.MapPost("/{slotIndex:int}", UploadAsync).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ProfileBannerLimits.MaximumMultipartBytes))
            .WithMetadata(new RequestFormLimitsAttribute
            {
                MultipartBodyLengthLimit = ProfileBannerLimits.MaximumMultipartBytes
            });
        presets.MapPatch("/{presetId:guid}", UpdateCropAsync);
        presets.MapDelete("/{presetId:guid}", DeleteAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/banner", DownloadActiveAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/banner/{presetId:guid}", DownloadPresetAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/banner/{presetId:guid}/source", DownloadSourceAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/banner-metadata", GetMetadataAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, IridiumDbContext db, SessionService sessions,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var values = await db.AccountBannerPresets.AsNoTracking()
            .Where(value => value.AccountId == session.AccountId).OrderBy(value => value.SlotIndex)
            .ToArrayAsync(cancellationToken);
        return Results.Ok(ToCollection(session.Account, values, context));
    }

    private static async Task<IResult> UploadAsync(int slotIndex, HttpContext context, IridiumDbContext db,
        SessionService sessions, IAttachmentStorage storage, IAvatarImageValidator validator,
        ProfileRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (slotIndex is < 0 or >= ProfileBannerLimits.MaximumPresets)
            return Results.BadRequest(new { message = $"Banner slots must be between 1 and {ProfileBannerLimits.MaximumPresets}." });
        if (!context.Request.HasFormContentType)
            return Results.BadRequest(new { message = "A multipart banner image is required." });
        IFormCollection form;
        try { form = await context.Request.ReadFormAsync(cancellationToken); }
        catch (Exception exception) when (exception is BadHttpRequestException or InvalidDataException)
        {
            return Results.BadRequest(new
            {
                message = $"This image is too large. The maximum banner size is {FileSizeDisplay.Megabytes(ProfileBannerLimits.MaximumUploadBytes)}."
            });
        }
        var file = form.Files.GetFile("file");
        if (file is null) return Results.BadRequest(new { message = "Choose a banner image." });
        if (file.Length > ProfileBannerLimits.MaximumUploadBytes)
            return Results.BadRequest(new { message = FileSizeDisplay.BannerTooLarge(file.Length) });
        var crop = Crop(form["cropX"], form["cropY"], form["zoom"]);
        if (crop is null) return Results.BadRequest(new { message = "The banner crop settings are invalid." });
        ValidatedAvatarImage image;
        try
        {
            await using var input = file.OpenReadStream();
            image = await validator.ValidateBannerAsync(input, file.ContentType, cancellationToken);
        }
        catch (AvatarImageValidationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        var existing = await db.AccountBannerPresets.SingleOrDefaultAsync(value =>
            value.AccountId == session.AccountId && value.SlotIndex == slotIndex, cancellationToken);
        if (existing is null && await db.AccountBannerPresets.CountAsync(value =>
                value.AccountId == session.AccountId, cancellationToken) >= ProfileBannerLimits.MaximumPresets)
            return Results.Conflict(new
            {
                message = $"An account may store at most {ProfileBannerLimits.MaximumPresets} banner presets."
            });

        var originalKey = Guid.NewGuid().ToString("N");
        string? processedKey = null;
        var processed = BannerImageProcessor.Process(image, crop.Value.X, crop.Value.Y, crop.Value.Zoom);
        try
        {
            await using (var source = new MemoryStream(image.Content, false))
                await storage.StoreAsync(originalKey, source, cancellationToken);
            if (processed is not null)
            {
                processedKey = Guid.NewGuid().ToString("N");
                await using var derivative = new MemoryStream(processed.Content, false);
                await storage.StoreAsync(processedKey, derivative, cancellationToken);
            }
        }
        catch
        {
            await storage.DeleteAsync(originalKey, cancellationToken);
            if (processedKey is not null) await storage.DeleteAsync(processedKey, cancellationToken);
            throw;
        }

        var oldOriginal = existing?.OriginalObjectKey;
        var oldProcessed = existing?.ProcessedObjectKey;
        var now = DateTimeOffset.UtcNow;
        var preset = existing ?? new AccountBannerPreset
        {
            Id = Guid.NewGuid(), AccountId = session.AccountId, Account = session.Account, SlotIndex = slotIndex,
            OriginalObjectKey = originalKey, ContentType = image.ContentType, CreatedAt = now
        };
        preset.OriginalObjectKey = originalKey;
        preset.ProcessedObjectKey = processedKey;
        preset.ContentType = image.ContentType;
        preset.SizeBytes = image.Content.LongLength;
        preset.Width = image.Width;
        preset.Height = image.Height;
        preset.CropX = crop.Value.X;
        preset.CropY = crop.Value.Y;
        preset.Zoom = crop.Value.Zoom;
        preset.Revision = NextRevision(preset.Revision);
        preset.UpdatedAt = now;
        if (existing is null) db.AccountBannerPresets.Add(preset);
        session.Account.ActiveBannerPresetId = preset.Id;
        session.Account.BannerRevision = NextRevision(session.Account.BannerRevision);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(originalKey, cancellationToken);
            if (processedKey is not null) await storage.DeleteAsync(processedKey, cancellationToken);
            throw;
        }
        await DeleteObjectsAsync(storage, oldOriginal, oldProcessed, originalKey, processedKey, cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        var all = await db.AccountBannerPresets.AsNoTracking().Where(value => value.AccountId == session.AccountId)
            .OrderBy(value => value.SlotIndex).ToArrayAsync(cancellationToken);
        return Results.Ok(ToCollection(session.Account, all, context));
    }

    private static async Task<IResult> UpdateCropAsync(Guid presetId, UpdateBannerCropRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions, IAttachmentStorage storage,
        IAvatarImageValidator validator, ProfileRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var crop = Crop(request.CropX, request.CropY, request.Zoom);
        if (crop is null) return Results.BadRequest(new { message = "The banner crop settings are invalid." });
        var preset = await db.AccountBannerPresets.SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == session.AccountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        var source = await storage.OpenReadAsync(preset.OriginalObjectKey, cancellationToken);
        if (source is null) return Results.Problem("The stored banner source is unavailable.");
        ValidatedAvatarImage image;
        await using (source)
            image = await validator.ValidateBannerAsync(source, preset.ContentType, cancellationToken);
        var processed = BannerImageProcessor.Process(image, crop.Value.X, crop.Value.Y, crop.Value.Zoom);
        string? processedKey = null;
        if (processed is not null)
        {
            processedKey = Guid.NewGuid().ToString("N");
            await using var derivative = new MemoryStream(processed.Content, false);
            await storage.StoreAsync(processedKey, derivative, cancellationToken);
        }
        var oldProcessed = preset.ProcessedObjectKey;
        preset.ProcessedObjectKey = processedKey;
        preset.CropX = crop.Value.X;
        preset.CropY = crop.Value.Y;
        preset.Zoom = crop.Value.Zoom;
        preset.Revision = NextRevision(preset.Revision);
        preset.UpdatedAt = DateTimeOffset.UtcNow;
        session.Account.ActiveBannerPresetId = preset.Id;
        session.Account.BannerRevision = NextRevision(session.Account.BannerRevision);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            if (processedKey is not null) await storage.DeleteAsync(processedKey, cancellationToken);
            throw;
        }
        if (oldProcessed is not null && oldProcessed != processedKey)
            await storage.DeleteAsync(oldProcessed, cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        return Results.Ok(ToDto(preset, context));
    }

    private static async Task<IResult> DeleteAsync(Guid presetId, HttpContext context, IridiumDbContext db,
        SessionService sessions, IAttachmentStorage storage, ProfileRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var preset = await db.AccountBannerPresets.SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == session.AccountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        var wasActive = session.Account.ActiveBannerPresetId == preset.Id;
        db.AccountBannerPresets.Remove(preset);
        if (wasActive)
        {
            session.Account.ActiveBannerPresetId = await db.AccountBannerPresets.AsNoTracking()
                .Where(value => value.AccountId == session.AccountId && value.Id != preset.Id)
                .OrderBy(value => value.SlotIndex).Select(value => (Guid?)value.Id)
                .FirstOrDefaultAsync(cancellationToken);
            session.Account.BannerRevision = NextRevision(session.Account.BannerRevision);
        }
        await db.SaveChangesAsync(cancellationToken);
        await DeleteObjectsAsync(storage, preset.OriginalObjectKey, preset.ProcessedObjectKey, null, null,
            cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMetadataAsync(Guid accountId, HttpContext context, IridiumDbContext db,
        CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().Where(value => value.Id == accountId)
            .Select(value => new { value.ActiveBannerPresetId, value.BannerRevision })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null) return Results.NotFound();
        if (account.ActiveBannerPresetId is not { } activeId)
            return Results.Ok(new ProfileBannerDto(false, null, null, account.BannerRevision));
        var preset = await db.AccountBannerPresets.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == activeId && value.AccountId == accountId, cancellationToken);
        if (preset is null) return Results.Ok(new ProfileBannerDto(false, null, null, account.BannerRevision));
        return Results.Ok(new ProfileBannerDto(true,
            Absolute(context, $"/api/profiles/{accountId}/banner?v={account.BannerRevision}"),
            Absolute(context, $"/api/profiles/{accountId}/banner/{preset.Id}/source?v={preset.Revision}"),
            account.BannerRevision, preset.CropX, preset.CropY, preset.Zoom, preset.Width, preset.Height,
            preset.ProcessedObjectKey is not null));
    }

    private static async Task<IResult> DownloadActiveAsync(Guid accountId, HttpContext context,
        IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken)
    {
        var activeId = await db.Accounts.AsNoTracking().Where(value => value.Id == accountId)
            .Select(value => value.ActiveBannerPresetId).SingleOrDefaultAsync(cancellationToken);
        return activeId is null ? Results.NotFound() :
            await DownloadCoreAsync(accountId, activeId.Value, false, context, db, storage, cancellationToken);
    }

    private static Task<IResult> DownloadPresetAsync(Guid accountId, Guid presetId, HttpContext context,
        IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken) =>
        DownloadCoreAsync(accountId, presetId, false, context, db, storage, cancellationToken);

    private static Task<IResult> DownloadSourceAsync(Guid accountId, Guid presetId, HttpContext context,
        IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken) =>
        DownloadCoreAsync(accountId, presetId, true, context, db, storage, cancellationToken);

    private static async Task<IResult> DownloadCoreAsync(Guid accountId, Guid presetId, bool source,
        HttpContext context, IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken)
    {
        var preset = await db.AccountBannerPresets.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == accountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        var useProcessed = !source && preset.ProcessedObjectKey is not null;
        var stream = await storage.OpenReadAsync(useProcessed ? preset.ProcessedObjectKey! : preset.OriginalObjectKey,
            cancellationToken);
        if (stream is null) return Results.NotFound();
        context.Response.Headers.CacheControl = context.Request.Query.ContainsKey("v")
            ? "public,max-age=31536000,immutable" : "no-cache";
        return Results.File(stream, useProcessed ? "image/webp" : preset.ContentType, enableRangeProcessing: true);
    }

    private static AccountBannerPresetsDto ToCollection(NodeAccount account,
        IEnumerable<AccountBannerPreset> presets, HttpContext context) =>
        new(account.Id, account.ActiveBannerPresetId, account.BannerRevision,
            presets.Select(value => ToDto(value, context)).ToArray());

    private static AccountBannerPresetDto ToDto(AccountBannerPreset value, HttpContext context) =>
        new(value.Id, value.SlotIndex,
            Absolute(context, $"/api/profiles/{value.AccountId}/banner/{value.Id}?v={value.Revision}"),
            Absolute(context, $"/api/profiles/{value.AccountId}/banner/{value.Id}/source?v={value.Revision}"),
            value.Revision, value.ContentType, value.Width, value.Height, value.CropX, value.CropY, value.Zoom,
            value.ProcessedObjectKey is not null, value.CreatedAt, value.UpdatedAt);

    private static string Absolute(HttpContext context, string path) =>
        $"{context.Request.Scheme}://{context.Request.Host}{path}";

    private static (double X, double Y, double Zoom)? Crop(string? x, string? y, string? zoom) =>
        double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) &&
        double.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py) &&
        double.TryParse(zoom, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pz)
            ? Crop(px, py, pz) : null;

    private static (double X, double Y, double Zoom)? Crop(double x, double y, double zoom) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(zoom) && x is >= -1 and <= 1 &&
        y is >= -1 and <= 1 && zoom is >= 1 and <= 3 ? (x, y, zoom) : null;

    private static long NextRevision(long current) => Math.Max(checked(current + 1), DateTimeOffset.UtcNow.UtcTicks);

    private static async Task DeleteObjectsAsync(IAttachmentStorage storage, string? original, string? processed,
        string? exceptOriginal, string? exceptProcessed, CancellationToken cancellationToken)
    {
        if (original is not null && original != exceptOriginal && original != exceptProcessed)
            await storage.DeleteAsync(original, cancellationToken);
        if (processed is not null && processed != original && processed != exceptOriginal && processed != exceptProcessed)
            await storage.DeleteAsync(processed, cancellationToken);
    }
}
