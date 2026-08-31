using Iridium.Protocol;
using Iridium.Server.Embeds;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class MessageDocumentEndpoints
{
    public static IEndpointRouteBuilder MapMessageDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/communities/{communityId:guid}/channels/{channelId:guid}/messages/{messageId:guid}/embed-documents/{documentId}",
            GetCommunityDocumentAsync);
        endpoints.MapGet(
            "/api/communities/{communityId:guid}/channels/{channelId:guid}/messages/{messageId:guid}/embed-documents/{documentId}/media/{mediaId}",
            GetCommunityDocumentMediaAsync);
        endpoints.MapGet(
            "/api/direct-messages/{conversationId:guid}/messages/{messageId:guid}/embed-documents/{documentId}",
            GetDirectDocumentAsync);
        endpoints.MapGet(
            "/api/direct-messages/{conversationId:guid}/messages/{messageId:guid}/embed-documents/{documentId}/media/{mediaId}",
            GetDirectDocumentMediaAsync);
        return endpoints;
    }

    private static async Task<IResult> GetCommunityDocumentAsync(Guid communityId, Guid channelId, Guid messageId,
        string documentId, bool? refresh, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IEmbeddedContentService documents,
        CancellationToken cancellationToken)
    {
        var source = await CommunitySourceAsync(communityId, channelId, messageId, documentId, context, db,
            sessions, authorization, cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(refresh == true
            ? await documents.RefreshAsync(source, cancellationToken)
            : await documents.GetAsync(source, cancellationToken));
    }

    private static async Task<IResult> GetCommunityDocumentMediaAsync(Guid communityId, Guid channelId,
        Guid messageId, string documentId, string mediaId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization,
        IEmbeddedContentService documents, CancellationToken cancellationToken)
    {
        var source = await CommunitySourceAsync(communityId, channelId, messageId, documentId, context, db,
            sessions, authorization, cancellationToken);
        if (source is null) return Results.NotFound();
        var media = await documents.GetMediaAsync(source, mediaId, cancellationToken);
        return media is null ? Results.NotFound() : Results.File(media.Bytes, media.ContentType,
            enableRangeProcessing: false);
    }

    private static async Task<EmbeddedContentConfiguration?> CommunitySourceAsync(Guid communityId, Guid channelId,
        Guid messageId, string documentId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return null;
        var message = await db.ChannelMessages.AsNoTracking().Where(value => value.Id == messageId &&
                value.CommunityId == communityId && value.ChannelId == channelId && !value.IsDeleted)
            .Select(value => new { value.Content, HostChannelId = value.Channel.ParentForumChannelId ?? value.ChannelId })
            .SingleOrDefaultAsync(cancellationToken);
        if (message is null) return null;
        var access = await authorization.GetChannelAccessAsync(communityId, message.HostChannelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels) ||
            !access.Has(CommunityPermission.ReadMessageHistory)) return null;
        return Source(message.Content, documentId);
    }

    private static async Task<IResult> GetDirectDocumentAsync(Guid conversationId, Guid messageId,
        string documentId, bool? refresh, HttpContext context, IridiumDbContext db, SessionService sessions,
        IEmbeddedContentService documents, CancellationToken cancellationToken)
    {
        var source = await DirectSourceAsync(conversationId, messageId, documentId, context, db, sessions,
            cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(refresh == true
            ? await documents.RefreshAsync(source, cancellationToken)
            : await documents.GetAsync(source, cancellationToken));
    }

    private static async Task<IResult> GetDirectDocumentMediaAsync(Guid conversationId, Guid messageId,
        string documentId, string mediaId, HttpContext context, IridiumDbContext db, SessionService sessions,
        IEmbeddedContentService documents, CancellationToken cancellationToken)
    {
        var source = await DirectSourceAsync(conversationId, messageId, documentId, context, db, sessions,
            cancellationToken);
        if (source is null) return Results.NotFound();
        var media = await documents.GetMediaAsync(source, mediaId, cancellationToken);
        return media is null ? Results.NotFound() : Results.File(media.Bytes, media.ContentType,
            enableRangeProcessing: false);
    }

    private static async Task<EmbeddedContentConfiguration?> DirectSourceAsync(Guid conversationId, Guid messageId,
        string documentId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return null;
        var participant = await db.DirectConversations.AsNoTracking().AnyAsync(value =>
            value.Id == conversationId && (value.ParticipantAAccountId == session.AccountId ||
                                            value.ParticipantBAccountId == session.AccountId), cancellationToken);
        if (!participant) return null;
        var message = await db.DirectMessages.AsNoTracking().Where(value => value.Id == messageId &&
                value.ConversationId == conversationId && !value.IsDeleted)
            .Select(value => value.Content).SingleOrDefaultAsync(cancellationToken);
        return message is null ? null : Source(message, documentId);
    }

    private static EmbeddedContentConfiguration? Source(string content, string documentId) =>
        CommunityChannelEmbeds.FindSupportedContent(content)
            .FirstOrDefault(value => string.Equals(value.RequestIdentity, documentId, StringComparison.Ordinal));
}
