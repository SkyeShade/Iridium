using Iridium.Protocol;
using Iridium.Server.Communities;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class CommunityMediaEndpoints
{
    public static IEndpointRouteBuilder MapCommunityMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}/media");
        group.MapGet("/avatar-presets", (Guid communityId, HttpContext c, IridiumDbContext db, SessionService s,
            CommunityAuthorizationService a, CancellationToken ct) => ListAvatarAsync(communityId,c,db,s,a,ct));
        group.MapGet("/banner-presets", (Guid communityId, HttpContext c, IridiumDbContext db, SessionService s,
            CommunityAuthorizationService a, CancellationToken ct) => ListBannerAsync(communityId,c,db,s,a,ct));
        group.MapPost("/{kind}/{slot:int}", UploadAsync).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ProfileBannerLimits.MaximumMultipartBytes))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = ProfileBannerLimits.MaximumMultipartBytes });
        group.MapPatch("/{kind}/{presetId:guid}", UpdateAsync);
        group.MapDelete("/{kind}/{presetId:guid}", DeleteAsync);
        endpoints.MapGet("/api/communities/{communityId:guid}/avatar", (Guid communityId, HttpContext c,
            IridiumDbContext db, IAttachmentStorage s, CancellationToken ct) => DownloadActiveAsync(communityId,CommunityMediaKind.Avatar,c,db,s,ct));
        endpoints.MapGet("/api/communities/{communityId:guid}/banner", (Guid communityId, HttpContext c,
            IridiumDbContext db, IAttachmentStorage s, CancellationToken ct) => DownloadActiveAsync(communityId,CommunityMediaKind.Banner,c,db,s,ct));
        endpoints.MapGet("/api/communities/{communityId:guid}/media/{kind}/{presetId:guid}", DownloadPresetAsync);
        endpoints.MapGet("/api/communities/{communityId:guid}/media/{kind}/{presetId:guid}/source", DownloadSourceAsync);
        return endpoints;
    }

    private static async Task<IResult> AuthorizeAsync(Guid communityId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        return await authorization.HasPermissionAsync(communityId, session.AccountId,
            CommunityPermission.ManageCommunity, db) ? Results.Ok() : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ListAvatarAsync(Guid id,HttpContext c,IridiumDbContext db,SessionService s,
        CommunityAuthorizationService a,CancellationToken ct)
    {
        var auth=await AuthorizeAsync(id,c,db,s,a); if (auth is not Microsoft.AspNetCore.Http.HttpResults.Ok) return auth;
        var community=await db.Communities.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct); if(community is null)return Results.NotFound();
        var rows=await db.CommunityMediaPresets.AsNoTracking().Where(x=>x.CommunityId==id&&x.Kind==CommunityMediaKind.Avatar).OrderBy(x=>x.SlotIndex).ToArrayAsync(ct);
        return Results.Ok(new AccountAvatarPresetsDto(id,community.ActiveAvatarPresetId,community.AvatarRevision,rows.Select(x=>AvatarDto(x,c)).ToArray()));
    }

    private static async Task<IResult> ListBannerAsync(Guid id,HttpContext c,IridiumDbContext db,SessionService s,
        CommunityAuthorizationService a,CancellationToken ct)
    {
        var auth=await AuthorizeAsync(id,c,db,s,a); if (auth is not Microsoft.AspNetCore.Http.HttpResults.Ok) return auth;
        var community=await db.Communities.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct); if(community is null)return Results.NotFound();
        var rows=await db.CommunityMediaPresets.AsNoTracking().Where(x=>x.CommunityId==id&&x.Kind==CommunityMediaKind.Banner).OrderBy(x=>x.SlotIndex).ToArrayAsync(ct);
        return Results.Ok(new AccountBannerPresetsDto(id,community.ActiveBannerPresetId,community.BannerRevision,rows.Select(x=>BannerDto(x,c)).ToArray()));
    }

    private static async Task<IResult> UploadAsync(Guid communityId,string kind,int slot,HttpContext context,
        IridiumDbContext db,SessionService sessions,CommunityAuthorizationService authorization,IAttachmentStorage storage,
        IAvatarImageValidator validator,CommunityRealtimePublisher realtime,CancellationToken ct)
    {
        var auth=await AuthorizeAsync(communityId,context,db,sessions,authorization); if(auth is not Microsoft.AspNetCore.Http.HttpResults.Ok)return auth;
        if(!TryKind(kind,out var mediaKind))return Results.BadRequest(new{message="Unknown Community media kind."});
        var limit=mediaKind==CommunityMediaKind.Avatar?CommunityMediaLimits.MaximumAvatarPresets:CommunityMediaLimits.MaximumBannerPresets;
        var byteLimit=mediaKind==CommunityMediaKind.Avatar?ProfileAvatarLimits.MaximumUploadBytes:ProfileBannerLimits.MaximumUploadBytes;
        if(slot<0||slot>=limit)return Results.BadRequest(new{message=$"{kind} slots must be between 1 and {limit}."});
        if(!context.Request.HasFormContentType)return Results.BadRequest(new{message="A multipart image is required."});
        IFormCollection form; try{form=await context.Request.ReadFormAsync(ct);}catch(Exception e) when(e is BadHttpRequestException or InvalidDataException){return Results.BadRequest(new{message=$"This image is too large. The maximum {kind} size is {FileSizeDisplay.Megabytes(byteLimit)}."});}
        var file=form.Files.GetFile("file"); if(file is null)return Results.BadRequest(new{message="Choose an image."});
        if(file.Length>byteLimit)return Results.BadRequest(new{message=mediaKind==CommunityMediaKind.Avatar?FileSizeDisplay.AvatarTooLarge(file.Length):FileSizeDisplay.BannerTooLarge(file.Length)});
        var crop=Crop(form["cropX"],form["cropY"],form["zoom"]);if(crop is null)return Results.BadRequest(new{message="The crop settings are invalid."});
        ValidatedAvatarImage image;try{await using var input=file.OpenReadStream();image=mediaKind==CommunityMediaKind.Avatar?await validator.ValidateAsync(input,file.ContentType,ct):await validator.ValidateBannerAsync(input,file.ContentType,ct);}catch(AvatarImageValidationException e){return Results.BadRequest(new{message=e.Message});}
        var existing=await db.CommunityMediaPresets.SingleOrDefaultAsync(x=>x.CommunityId==communityId&&x.Kind==mediaKind&&x.SlotIndex==slot,ct);
        if(existing is null&&await db.CommunityMediaPresets.CountAsync(x=>x.CommunityId==communityId&&x.Kind==mediaKind,ct)>=limit)return Results.Conflict(new{message=$"A Community may store at most {limit} {kind} presets."});
        var originalKey=Guid.NewGuid().ToString("N");string? processedKey=null;var processed=mediaKind==CommunityMediaKind.Banner
            ? BannerImageProcessor.Process(image,crop.Value.X,crop.Value.Y,crop.Value.Zoom,
                CommunityBannerLimits.CropWidth,CommunityBannerLimits.CropHeight,
                CommunityBannerLimits.ProcessedWidth,CommunityBannerLimits.ProcessedHeight):null;
        try{await using(var source=new MemoryStream(image.Content,false))await storage.StoreAsync(originalKey,source,ct);if(processed is not null){processedKey=Guid.NewGuid().ToString("N");await using var derivative=new MemoryStream(processed.Content,false);await storage.StoreAsync(processedKey,derivative,ct);}}catch{await storage.DeleteAsync(originalKey,ct);if(processedKey is not null)await storage.DeleteAsync(processedKey,ct);throw;}
        var community=await db.Communities.SingleAsync(x=>x.Id==communityId,ct);var now=DateTimeOffset.UtcNow;var oldOriginal=existing?.OriginalObjectKey;var oldProcessed=existing?.ProcessedObjectKey;
        var preset=existing??new CommunityMediaPreset{Id=Guid.NewGuid(),CommunityId=communityId,Community=community,Kind=mediaKind,SlotIndex=slot,OriginalObjectKey=originalKey,ContentType=image.ContentType,CreatedAt=now};
        preset.OriginalObjectKey=originalKey;preset.ProcessedObjectKey=processedKey;preset.ContentType=image.ContentType;preset.SizeBytes=image.Content.LongLength;preset.Width=image.Width;preset.Height=image.Height;preset.CropX=crop.Value.X;preset.CropY=crop.Value.Y;preset.Zoom=crop.Value.Zoom;preset.Revision=Next(preset.Revision);preset.UpdatedAt=now;if(existing is null)db.CommunityMediaPresets.Add(preset);
        if(mediaKind==CommunityMediaKind.Avatar){community.ActiveAvatarPresetId=preset.Id;community.AvatarRevision=Next(community.AvatarRevision);}else{community.ActiveBannerPresetId=preset.Id;community.BannerRevision=Next(community.BannerRevision);}
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            await DeleteObjects(storage, originalKey, processedKey, null, null, ct);
            throw;
        }
        await DeleteObjects(storage,oldOriginal,oldProcessed,originalKey,processedKey,ct);await realtime.PublishAsync(communityId,"identity-updated",db,ct);
        return mediaKind==CommunityMediaKind.Avatar?await ListAvatarAsync(communityId,context,db,sessions,authorization,ct):await ListBannerAsync(communityId,context,db,sessions,authorization,ct);
    }

    private static async Task<IResult> UpdateAsync(Guid communityId,string kind,Guid presetId,UpdateAvatarCropRequest request,HttpContext context,IridiumDbContext db,SessionService sessions,CommunityAuthorizationService authorization,IAttachmentStorage storage,IAvatarImageValidator validator,CommunityRealtimePublisher realtime,CancellationToken ct)
    {
        var auth=await AuthorizeAsync(communityId,context,db,sessions,authorization);if(auth is not Microsoft.AspNetCore.Http.HttpResults.Ok)return auth;if(!TryKind(kind,out var mediaKind))return Results.BadRequest();var crop=Crop(request.CropX,request.CropY,request.Zoom);if(crop is null)return Results.BadRequest(new{message="The crop settings are invalid."});
        var preset=await db.CommunityMediaPresets.SingleOrDefaultAsync(x=>x.Id==presetId&&x.CommunityId==communityId&&x.Kind==mediaKind,ct);if(preset is null)return Results.NotFound();var community=await db.Communities.SingleAsync(x=>x.Id==communityId,ct);
        string? oldProcessed = null;
        string? newProcessed = null;
        if(mediaKind==CommunityMediaKind.Banner){var source=await storage.OpenReadAsync(preset.OriginalObjectKey,ct);if(source is null)return Results.NotFound();ValidatedAvatarImage image;await using(source)image=await validator.ValidateBannerAsync(source,preset.ContentType,ct);var processed=BannerImageProcessor.Process(image,crop.Value.X,crop.Value.Y,crop.Value.Zoom,CommunityBannerLimits.CropWidth,CommunityBannerLimits.CropHeight,CommunityBannerLimits.ProcessedWidth,CommunityBannerLimits.ProcessedHeight);oldProcessed=preset.ProcessedObjectKey;preset.ProcessedObjectKey=null;if(processed is not null){newProcessed=Guid.NewGuid().ToString("N");preset.ProcessedObjectKey=newProcessed;await using var stream=new MemoryStream(processed.Content,false);await storage.StoreAsync(newProcessed,stream,ct);}}
        preset.CropX=crop.Value.X;preset.CropY=crop.Value.Y;preset.Zoom=crop.Value.Zoom;preset.Revision=Next(preset.Revision);preset.UpdatedAt=DateTimeOffset.UtcNow;if(mediaKind==CommunityMediaKind.Avatar){community.ActiveAvatarPresetId=preset.Id;community.AvatarRevision=Next(community.AvatarRevision);}else{community.ActiveBannerPresetId=preset.Id;community.BannerRevision=Next(community.BannerRevision);}
        try { await db.SaveChangesAsync(ct); }
        catch { if(newProcessed is not null)await storage.DeleteAsync(newProcessed,ct);throw; }
        if(oldProcessed is not null&&oldProcessed!=newProcessed)await storage.DeleteAsync(oldProcessed,ct);
        await realtime.PublishAsync(communityId,"identity-updated",db,ct);return Results.Ok(mediaKind==CommunityMediaKind.Avatar?(object)AvatarDto(preset,context):BannerDto(preset,context));
    }

    private static async Task<IResult> DeleteAsync(Guid communityId,string kind,Guid presetId,HttpContext context,IridiumDbContext db,SessionService sessions,CommunityAuthorizationService authorization,IAttachmentStorage storage,CommunityRealtimePublisher realtime,CancellationToken ct)
    {
        var auth=await AuthorizeAsync(communityId,context,db,sessions,authorization);if(auth is not Microsoft.AspNetCore.Http.HttpResults.Ok)return auth;if(!TryKind(kind,out var mediaKind))return Results.BadRequest();var preset=await db.CommunityMediaPresets.SingleOrDefaultAsync(x=>x.Id==presetId&&x.CommunityId==communityId&&x.Kind==mediaKind,ct);if(preset is null)return Results.NotFound();var community=await db.Communities.SingleAsync(x=>x.Id==communityId,ct);db.Remove(preset);
        var fallback=await db.CommunityMediaPresets.AsNoTracking().Where(x=>x.CommunityId==communityId&&x.Kind==mediaKind&&x.Id!=presetId).OrderBy(x=>x.SlotIndex).Select(x=>(Guid?)x.Id).FirstOrDefaultAsync(ct);if(mediaKind==CommunityMediaKind.Avatar&&community.ActiveAvatarPresetId==presetId){community.ActiveAvatarPresetId=fallback;community.AvatarRevision=Next(community.AvatarRevision);}if(mediaKind==CommunityMediaKind.Banner&&community.ActiveBannerPresetId==presetId){community.ActiveBannerPresetId=fallback;community.BannerRevision=Next(community.BannerRevision);}await db.SaveChangesAsync(ct);await DeleteObjects(storage,preset.OriginalObjectKey,preset.ProcessedObjectKey,null,null,ct);await realtime.PublishAsync(communityId,"identity-updated",db,ct);return Results.NoContent();
    }

    private static Task<IResult> DownloadPresetAsync(Guid communityId,string kind,Guid presetId,HttpContext c,IridiumDbContext db,IAttachmentStorage s,CancellationToken ct)=>DownloadAsync(communityId,kind,presetId,false,c,db,s,ct);
    private static Task<IResult> DownloadSourceAsync(Guid communityId,string kind,Guid presetId,HttpContext c,IridiumDbContext db,IAttachmentStorage s,CancellationToken ct)=>DownloadAsync(communityId,kind,presetId,true,c,db,s,ct);
    private static async Task<IResult> DownloadActiveAsync(Guid id,CommunityMediaKind kind,HttpContext c,IridiumDbContext db,IAttachmentStorage s,CancellationToken ct){var active=await db.Communities.AsNoTracking().Where(x=>x.Id==id).Select(x=>kind==CommunityMediaKind.Avatar?x.ActiveAvatarPresetId:x.ActiveBannerPresetId).SingleOrDefaultAsync(ct);return active is null?Results.NotFound():await DownloadCore(id,kind,active.Value,false,c,db,s,ct);}
    private static async Task<IResult> DownloadAsync(Guid id,string kind,Guid preset,bool source,HttpContext c,IridiumDbContext db,IAttachmentStorage s,CancellationToken ct)=>!TryKind(kind,out var parsed)?Results.NotFound():await DownloadCore(id,parsed,preset,source,c,db,s,ct);
    private static async Task<IResult> DownloadCore(Guid id,CommunityMediaKind kind,Guid presetId,bool source,HttpContext c,IridiumDbContext db,IAttachmentStorage s,CancellationToken ct){var row=await db.CommunityMediaPresets.AsNoTracking().SingleOrDefaultAsync(x=>x.CommunityId==id&&x.Kind==kind&&x.Id==presetId,ct);if(row is null)return Results.NotFound();var processed=!source&&row.ProcessedObjectKey is not null;var stream=await s.OpenReadAsync(processed?row.ProcessedObjectKey!:row.OriginalObjectKey,ct);if(stream is null)return Results.NotFound();c.Response.Headers.CacheControl=c.Request.Query.ContainsKey("v")?"public,max-age=31536000,immutable":"no-cache";return Results.File(stream,processed?"image/webp":row.ContentType,enableRangeProcessing:true);}
    private static AccountAvatarPresetDto AvatarDto(CommunityMediaPreset x,HttpContext c)=>new(x.Id,x.SlotIndex,Absolute(c,$"/api/communities/{x.CommunityId}/media/avatar/{x.Id}?v={x.Revision}"),x.Revision,x.ContentType,x.Width,x.Height,x.CropX,x.CropY,x.Zoom,x.CreatedAt,x.UpdatedAt);
    private static AccountBannerPresetDto BannerDto(CommunityMediaPreset x,HttpContext c)=>new(x.Id,x.SlotIndex,Absolute(c,$"/api/communities/{x.CommunityId}/media/banner/{x.Id}?v={x.Revision}"),Absolute(c,$"/api/communities/{x.CommunityId}/media/banner/{x.Id}/source?v={x.Revision}"),x.Revision,x.ContentType,x.Width,x.Height,x.CropX,x.CropY,x.Zoom,x.ProcessedObjectKey is not null,x.CreatedAt,x.UpdatedAt);
    private static string Absolute(HttpContext c,string path)=>$"{c.Request.Scheme}://{c.Request.Host}{path}";
    private static bool TryKind(string value,out CommunityMediaKind kind)=>Enum.TryParse(value,true,out kind);
    private static (double X,double Y,double Zoom)? Crop(string? x,string? y,string? z)=>double.TryParse(x,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var px)&&double.TryParse(y,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var py)&&double.TryParse(z,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var pz)?Crop(px,py,pz):null;
    private static (double X,double Y,double Zoom)? Crop(double x,double y,double z)=>double.IsFinite(x)&&double.IsFinite(y)&&double.IsFinite(z)&&x is>=-1 and<=1&&y is>=-1 and<=1&&z is>=1 and<=3?(x,y,z):null;
    private static long Next(long current)=>Math.Max(checked(current+1),DateTimeOffset.UtcNow.UtcTicks);
    private static async Task DeleteObjects(IAttachmentStorage s,string? original,string? processed,string? keepOriginal,string? keepProcessed,CancellationToken ct){if(original is not null&&original!=keepOriginal&&original!=keepProcessed)await s.DeleteAsync(original,ct);if(processed is not null&&processed!=original&&processed!=keepOriginal&&processed!=keepProcessed)await s.DeleteAsync(processed,ct);}
}
