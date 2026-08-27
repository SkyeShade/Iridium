using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Profiles;
using Iridium.Server.Communities;
using Iridium.Server.Hubs;
using Iridium.Server.Security;
using Iridium.Server.Storage;
using Iridium.Server.Voice;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Iridium.Server.Api;

public static class AvatarPresetEndpoints
{
    // This is a storage/abuse guard, not a user-facing slot model. The UI grows dynamically.
    public const int MaximumPresets = 256;

    public static IEndpointRouteBuilder MapAvatarPresetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var presets = endpoints.MapGroup("/api/account/avatar-presets");
        presets.MapGet("/", ListAsync);
        presets.MapPost("/{slotIndex:int}", UploadAsync).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ProfileAvatarLimits.MaximumMultipartBytes))
            .WithMetadata(new RequestFormLimitsAttribute
            {
                MultipartBodyLengthLimit = ProfileAvatarLimits.MaximumMultipartBytes
            });
        presets.MapPatch("/{presetId:guid}", UpdateCropAsync);
        presets.MapPut("/{presetId:guid}/active", ActivateAsync);
        presets.MapDelete("/active", ClearActiveAsync);
        presets.MapDelete("/{presetId:guid}", DeleteAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/avatar", DownloadActiveAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/avatar/{presetId:guid}", DownloadPresetAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/avatar/{presetId:guid}/metadata", GetPresetMetadataAsync);
        endpoints.MapGet("/api/profiles/{accountId:guid}/avatar-metadata", GetMetadataAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, IridiumDbContext db, SessionService sessions,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var presets = await db.AccountAvatarPresets.AsNoTracking()
            .Where(value => value.AccountId == session.AccountId).OrderBy(value => value.SlotIndex)
            .ToArrayAsync(cancellationToken);
        return Results.Ok(ToCollection(session.Account, presets, context));
    }

    private static async Task<IResult> UploadAsync(int slotIndex, HttpContext context, IridiumDbContext db,
        SessionService sessions, IAttachmentStorage storage, IAvatarImageValidator validator,
        ProfileRealtimePublisher realtime, CommunityRealtimePublisher communityRealtime,
        IHostEnvironment environment, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (slotIndex is < 0 or >= MaximumPresets)
            return Results.BadRequest(new { message = $"Avatar slots must be between 1 and {MaximumPresets}." });
        if (!context.Request.HasFormContentType)
            return Results.BadRequest(new { message = "A multipart avatar image is required." });
        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is BadHttpRequestException or InvalidDataException)
        {
            return Results.BadRequest(new
            {
                message = $"This image is too large. The maximum avatar size is {FileSizeDisplay.Megabytes(ProfileAvatarLimits.MaximumUploadBytes)}."
            });
        }
        var file = form.Files.GetFile("file");
        if (file is null) return Results.BadRequest(new { message = "Choose an avatar image." });
        if (file.Length > ProfileAvatarLimits.MaximumUploadBytes)
            return Results.BadRequest(new
            {
                message = FileSizeDisplay.AvatarTooLarge(file.Length, ProfileAvatarLimits.MaximumUploadBytes)
            });
        if (environment.IsDevelopment())
            loggerFactory.CreateLogger("Iridium.AvatarUpload").LogInformation(
                "AVATAR UPLOAD Multipart FileName={FileName} DeclaredContentType={ContentType} Size={Size}",
                file.FileName, file.ContentType, file.Length);
        ValidatedAvatarImage image;
        try
        {
            await using var input = file.OpenReadStream();
            image = await validator.ValidateAsync(input, file.ContentType, cancellationToken);
        }
        catch (AvatarImageValidationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }

        var crop = Crop(form["cropX"], form["cropY"], form["zoom"]);
        if (crop is null) return Results.BadRequest(new { message = "The avatar crop settings are invalid." });
        var setActive = bool.TryParse(form["setActive"], out var requestedActive) && requestedActive;
        var existing = await db.AccountAvatarPresets.SingleOrDefaultAsync(value =>
            value.AccountId == session.AccountId && value.SlotIndex == slotIndex, cancellationToken);
        if (existing is null && await db.AccountAvatarPresets.CountAsync(value =>
                value.AccountId == session.AccountId, cancellationToken) >= MaximumPresets)
            return Results.Conflict(new { message = $"An account may store at most {MaximumPresets} avatar presets." });

        var objectKey = Guid.NewGuid().ToString("N");
        try
        {
            await using var imageStream = new MemoryStream(image.Content, writable: false);
            await storage.StoreAsync(objectKey, imageStream, cancellationToken);
        }
        catch { await storage.DeleteAsync(objectKey, cancellationToken); throw; }

        var now = DateTimeOffset.UtcNow;
        var oldObjectKey = existing?.OriginalObjectKey;
        var oldProcessedKey = existing?.ProcessedObjectKey;
        var preset = existing ?? new AccountAvatarPreset
        {
            Id = Guid.NewGuid(), AccountId = session.AccountId, Account = session.Account,
            SlotIndex = slotIndex, OriginalObjectKey = objectKey, ContentType = image.ContentType,
            CreatedAt = now
        };
        preset.OriginalObjectKey = objectKey;
        preset.ProcessedObjectKey = null;
        preset.ContentType = image.ContentType;
        preset.SizeBytes = image.Content.LongLength;
        preset.Width = image.Width;
        preset.Height = image.Height;
        preset.CropX = crop.Value.X;
        preset.CropY = crop.Value.Y;
        preset.Zoom = crop.Value.Zoom;
        preset.Revision = NextRevision(preset.Revision);
        preset.UpdatedAt = now;
        if (existing is null) db.AccountAvatarPresets.Add(preset);
        if (setActive)
        {
            session.Account.ActiveAvatarPresetId = preset.Id;
            session.Account.AvatarRevision = NextRevision(session.Account.AvatarRevision);
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(objectKey, cancellationToken);
            throw;
        }
        await DeleteOldObjectsAsync(db, storage, oldObjectKey, oldProcessedKey, objectKey, cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        await PublishAssignedCommunitiesForAvatarAsync(preset.Id, db, communityRealtime, cancellationToken);
        var all = await db.AccountAvatarPresets.AsNoTracking().Where(value => value.AccountId == session.AccountId)
            .OrderBy(value => value.SlotIndex).ToArrayAsync(cancellationToken);
        return Results.Ok(ToCollection(session.Account, all, context));
    }

    private static async Task<IResult> UpdateCropAsync(Guid presetId, UpdateAvatarCropRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions, ProfileRealtimePublisher realtime,
        CommunityRealtimePublisher communityRealtime,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var crop = Crop(request.CropX, request.CropY, request.Zoom);
        if (crop is null) return Results.BadRequest(new { message = "The avatar crop settings are invalid." });
        var preset = await db.AccountAvatarPresets.SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == session.AccountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        preset.CropX = crop.Value.X; preset.CropY = crop.Value.Y; preset.Zoom = crop.Value.Zoom;
        preset.Revision = NextRevision(preset.Revision); preset.UpdatedAt = DateTimeOffset.UtcNow;
        if (request.SetActive || session.Account.ActiveAvatarPresetId == preset.Id)
        {
            session.Account.ActiveAvatarPresetId = preset.Id;
            session.Account.AvatarRevision = NextRevision(session.Account.AvatarRevision);
        }
        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        await PublishAssignedCommunitiesForAvatarAsync(preset.Id, db, communityRealtime, cancellationToken);
        return Results.Ok(ToDto(preset, context));
    }

    private static async Task<IResult> ActivateAsync(Guid presetId, HttpContext context, IridiumDbContext db,
        SessionService sessions, ProfileRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var preset = await db.AccountAvatarPresets.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == session.AccountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        session.Account.ActiveAvatarPresetId = preset.Id;
        session.Account.AvatarRevision = NextRevision(session.Account.AvatarRevision);
        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        return Results.Ok(new { session.Account.ActiveAvatarPresetId, session.Account.AvatarRevision });
    }

    private static async Task<IResult> ClearActiveAsync(HttpContext context, IridiumDbContext db,
        SessionService sessions, ProfileRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (session.Account.ActiveAvatarPresetId is null) return Results.NoContent();
        session.Account.ActiveAvatarPresetId = null;
        session.Account.AvatarRevision = NextRevision(session.Account.AvatarRevision);
        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid presetId, HttpContext context, IridiumDbContext db,
        SessionService sessions, IAttachmentStorage storage, ProfileRealtimePublisher realtime,
        CommunityRealtimePublisher communityRealtime, CommunityVoiceRoomService voiceRooms,
        IHubContext<ChatHub> hub,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var preset = await db.AccountAvatarPresets.SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == session.AccountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var wasActive = session.Account.ActiveAvatarPresetId == preset.Id;
        var profilePresetIds = await db.UserProfilePresets.Where(value => value.AvatarPresetId == preset.Id)
            .Select(value => value.Id).ToArrayAsync(cancellationToken);
        var assignedCommunityIds = await db.CommunityMembers.Where(value =>
                value.ProfilePresetId != null && profilePresetIds.Contains(value.ProfilePresetId.Value))
            .Select(value => value.CommunityId).Distinct().ToArrayAsync(cancellationToken);
        await db.UserProfilePresets.Where(value => value.AvatarPresetId == preset.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.AvatarPresetId, (Guid?)null), cancellationToken);
        db.AccountAvatarPresets.Remove(preset);
        if (wasActive)
        {
            session.Account.ActiveAvatarPresetId = await db.AccountAvatarPresets.AsNoTracking()
                .Where(value => value.AccountId == session.AccountId && value.Id != preset.Id)
                .OrderBy(value => value.SlotIndex).Select(value => (Guid?)value.Id)
                .FirstOrDefaultAsync(cancellationToken);
            session.Account.AvatarRevision = NextRevision(session.Account.AvatarRevision);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await DeleteOldObjectsAsync(db, storage, preset.OriginalObjectKey, preset.ProcessedObjectKey, null,
            cancellationToken);
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db, cancellationToken);
        foreach (var communityId in assignedCommunityIds)
        {
            var member = await db.CommunityMembers.AsNoTracking().Include(value => value.Account)
                .Include(value => value.ProfilePreset).ThenInclude(value => value!.AvatarPreset)
                .SingleAsync(value => value.CommunityId == communityId && value.AccountId == session.AccountId,
                    cancellationToken);
            var profile = ChannelMessageMapper.ValidPreset(member);
            var voiceChanges = voiceRooms.UpdateDisplayProfile(communityId, session.AccountId,
                ChannelMessageMapper.ResolveDisplayName(member), profile?.AvatarPresetId,
                profile?.AvatarPreset?.Revision ?? member.Account.AvatarRevision);
            if (voiceChanges.Count > 0)
            {
                var recipients = await db.CommunityMembers.AsNoTracking()
                    .Where(value => value.CommunityId == communityId).Select(value => value.AccountId)
                    .Distinct().ToArrayAsync(cancellationToken);
                foreach (var change in voiceChanges)
                    await hub.Clients.Groups(recipients.Select(ChatHub.AccountGroup).ToArray()).SendAsync(
                        CommunityVoiceHubContract.ParticipantStateChanged, change, cancellationToken);
            }
            await communityRealtime.PublishAsync(communityId, "member-profile-updated", db, cancellationToken);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadActiveAsync(Guid accountId, HttpContext context,
        IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken)
    {
        var activeId = await db.Accounts.AsNoTracking().Where(value => value.Id == accountId)
            .Select(value => value.ActiveAvatarPresetId).SingleOrDefaultAsync(cancellationToken);
        if (activeId is null) return Results.NotFound();
        return await DownloadPresetCoreAsync(accountId, activeId.Value, context, db, storage, cancellationToken);
    }

    private static async Task<IResult> GetMetadataAsync(Guid accountId, HttpContext context, IridiumDbContext db,
        CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().Where(value => value.Id == accountId)
            .Select(value => new { value.ActiveAvatarPresetId, value.AvatarRevision })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null) return Results.NotFound();
        if (account.ActiveAvatarPresetId is not { } activeId)
            return Results.Ok(new ProfileAvatarDto(false, null, account.AvatarRevision));
        var preset = await db.AccountAvatarPresets.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == activeId && value.AccountId == accountId, cancellationToken);
        if (preset is null) return Results.Ok(new ProfileAvatarDto(false, null, account.AvatarRevision));
        var url = $"{context.Request.Scheme}://{context.Request.Host}/api/profiles/{accountId}/avatar?v={account.AvatarRevision}";
        return Results.Ok(new ProfileAvatarDto(true, url, account.AvatarRevision,
            preset.CropX, preset.CropY, preset.Zoom, preset.Width, preset.Height));
    }

    private static Task<IResult> DownloadPresetAsync(Guid accountId, Guid presetId, HttpContext context,
        IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken) =>
        DownloadPresetCoreAsync(accountId, presetId, context, db, storage, cancellationToken);

    private static async Task<IResult> GetPresetMetadataAsync(Guid accountId, Guid presetId, HttpContext context,
        IridiumDbContext db, CancellationToken cancellationToken)
    {
        var preset = await db.AccountAvatarPresets.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == accountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        var url = $"{context.Request.Scheme}://{context.Request.Host}/api/profiles/{accountId}/avatar/{presetId}?v={preset.Revision}";
        return Results.Ok(new ProfileAvatarDto(true, url, preset.Revision,
            preset.CropX, preset.CropY, preset.Zoom, preset.Width, preset.Height));
    }

    private static async Task<IResult> DownloadPresetCoreAsync(Guid accountId, Guid presetId, HttpContext context,
        IridiumDbContext db, IAttachmentStorage storage, CancellationToken cancellationToken)
    {
        var preset = await db.AccountAvatarPresets.AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == accountId, cancellationToken);
        if (preset is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(preset.ProcessedObjectKey ?? preset.OriginalObjectKey, cancellationToken);
        if (stream is null) return Results.NotFound();
        if (context.Request.Query.ContainsKey("v"))
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        else context.Response.Headers.CacheControl = "no-cache";
        return Results.File(stream, preset.ContentType, enableRangeProcessing: true);
    }

    private static AccountAvatarPresetsDto ToCollection(NodeAccount account,
        IEnumerable<AccountAvatarPreset> presets, HttpContext context) =>
        new(account.Id, account.ActiveAvatarPresetId, account.AvatarRevision,
            presets.Select(value => ToDto(value, context)).ToArray());

    internal static AccountAvatarPresetDto ToDto(AccountAvatarPreset value, HttpContext context) =>
        new(value.Id, value.SlotIndex,
            $"{context.Request.Scheme}://{context.Request.Host}/api/profiles/{value.AccountId}/avatar/{value.Id}?v={value.Revision}",
            value.Revision, value.ContentType, value.Width, value.Height, value.CropX, value.CropY, value.Zoom,
            value.CreatedAt, value.UpdatedAt, value.DisplayName);

    private static async Task PublishAssignedCommunitiesForAvatarAsync(Guid avatarPresetId, IridiumDbContext db,
        CommunityRealtimePublisher realtime, CancellationToken cancellationToken)
    {
        var profilePresetIds = await db.UserProfilePresets.AsNoTracking()
            .Where(value => value.AvatarPresetId == avatarPresetId).Select(value => value.Id).ToArrayAsync(cancellationToken);
        var communityIds = await db.CommunityMembers.AsNoTracking()
            .Where(value => value.ProfilePresetId != null && profilePresetIds.Contains(value.ProfilePresetId.Value))
            .Select(value => value.CommunityId)
            .Distinct().ToArrayAsync(cancellationToken);
        foreach (var communityId in communityIds)
            await realtime.PublishAsync(communityId, "member-profile-updated", db, cancellationToken);
    }

    private static (double X, double Y, double Zoom)? Crop(string? x, string? y, string? zoom) =>
        double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedX) &&
        double.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedY) &&
        double.TryParse(zoom, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedZoom)
            ? Crop(parsedX, parsedY, parsedZoom) : null;

    private static (double X, double Y, double Zoom)? Crop(double x, double y, double zoom) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(zoom) &&
        x is >= -1 and <= 1 && y is >= -1 and <= 1 && zoom is >= 1 and <= 3
            ? (x, y, zoom) : null;

    private static long NextRevision(long current) => Math.Max(checked(current + 1), DateTimeOffset.UtcNow.UtcTicks);

    private static async Task DeleteOldObjectsAsync(IridiumDbContext db, IAttachmentStorage storage,
        string? original, string? processed, string? except, CancellationToken cancellationToken)
    {
        if (original is not null && original != except && !await IsHistoricalMessageAvatarAsync(db, original, cancellationToken))
            await storage.DeleteAsync(original, cancellationToken);
        if (processed is not null && processed != original && processed != except &&
            !await IsHistoricalMessageAvatarAsync(db, processed, cancellationToken))
            await storage.DeleteAsync(processed, cancellationToken);
    }

    private static Task<bool> IsHistoricalMessageAvatarAsync(
        IridiumDbContext db, string objectKey, CancellationToken cancellationToken) =>
        db.ChannelMessages.AsNoTracking().AnyAsync(
            value => value.AuthorAvatarObjectKeySnapshot == objectKey, cancellationToken);
}
