using System.Text.Json;
using Iridium.Protocol;
using Iridium.Server.Configuration;
using Iridium.Server.Domain;
using Iridium.Server.Embeds;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Api;

public static class CommunityForumEndpoints
{
    private const int DefaultPageSize = 30;
    private const int MaximumPageSize = 50;
    private const int MaximumTitleLength = 120;

    public static IEndpointRouteBuilder MapCommunityForumEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}/forums/{channelId:guid}/posts");
        group.MapGet("/", ListAsync);
        group.MapGet("/{postId:guid}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{postId:guid}", UpdateAsync);
        group.MapGet("/{postId:guid}/embed-document", GetEmbedDocumentAsync);
        group.MapGet("/{postId:guid}/embed-document/media/{mediaId}", GetEmbedDocumentMediaAsync);
        group.MapDelete("/{postId:guid}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> GetEmbedDocumentAsync(Guid communityId, Guid channelId, Guid postId,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IGoogleDocsPublishedDocumentService documents,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var post = await db.CommunityForumPosts.AsNoTracking().SingleOrDefaultAsync(value => value.Id == postId &&
            value.CommunityId == communityId && value.ForumChannelId == channelId, cancellationToken);
        if (post is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        if (!CommunityChannelEmbeds.TryResolve(post.EmbedProvider, post.EmbedUrl, out var configuration) ||
            configuration?.FetchUrl is null)
            return Results.Ok(new ChannelEmbedDocumentDto(ChannelEmbedDocumentStatus.Unsupported, null));
        return Results.Ok(await documents.GetAsync(configuration, cancellationToken));
    }

    private static async Task<IResult> GetEmbedDocumentMediaAsync(Guid communityId, Guid channelId, Guid postId,
        string mediaId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IGoogleDocsPublishedDocumentService documents,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var post = await db.CommunityForumPosts.AsNoTracking().SingleOrDefaultAsync(value => value.Id == postId &&
            value.CommunityId == communityId && value.ForumChannelId == channelId, cancellationToken);
        if (post is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels) ||
            !CommunityChannelEmbeds.TryResolve(post.EmbedProvider, post.EmbedUrl, out var configuration) ||
            configuration?.FetchUrl is null) return Results.NotFound();
        var media = await documents.GetMediaAsync(configuration, mediaId, cancellationToken);
        return media is null ? Results.NotFound() : Results.File(media.Bytes, media.ContentType,
            enableRangeProcessing: false);
    }

    private static async Task<IResult> ListAsync(Guid communityId, Guid channelId, int? offset, int? limit,
        string? search, string? tags,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsForumVisibleAsync(communityId, channelId, session.AccountId, db, authorization))
            return Results.NotFound();
        var skip = Math.Max(0, offset ?? 0);
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaximumPageSize);
        var query = db.CommunityForumPosts.AsNoTracking()
            .Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage)
            .Where(value => value.CommunityId == communityId && value.ForumChannelId == channelId);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var normalizedTerm = term.ToLower();
            query = query.Where(value => value.Title.ToLower().Contains(normalizedTerm) ||
                value.RootMessage.Content.ToLower().Contains(normalizedTerm) ||
                value.AuthorAccount.DisplayName.ToLower().Contains(normalizedTerm) ||
                value.AuthorAccount.Username.ToLower().Contains(normalizedTerm));
        }
        var selectedTags = ParseTagIds(tags);
        if (selectedTags is null) return Invalid("One or more tag filters are invalid.");
        if (selectedTags.Count > 0)
        {
            var available = await db.CommunityForumTags.Where(value => value.ChannelId == channelId &&
                selectedTags.Contains(value.Id)).Select(value => value.Id).CountAsync();
            if (available != selectedTags.Count) return Invalid("One or more tag filters do not belong to this Forum.");
            // Discord's multi-tag filter is union/OR: a post matching any selected tag is included.
            query = query.Where(value => db.CommunityForumPostTags.Any(assignment =>
                assignment.PostId == value.Id && selectedTags.Contains(assignment.TagId)));
        }
        var posts = await query
            .OrderByDescending(value => value.IsPinned)
            .ThenByDescending(value => value.LastActivityAt)
            .ThenByDescending(value => value.Id)
            .Skip(skip).Take(take + 1).ToListAsync();
        var hasMore = posts.Count > take;
        if (hasMore) posts.RemoveAt(posts.Count - 1);
        var unread = await UnreadCountsAsync(posts, session.AccountId, db);
        var tagMap = await LoadPostTagsAsync(posts.Select(value => value.Id).ToArray(), db);
        return Results.Ok(new CommunityForumPostPageDto(
            posts.Select(value => ToDto(value, unread.GetValueOrDefault(value.Id),
                tagMap.GetValueOrDefault(value.Id), includeEmbedUrl: false)).ToArray(),
            hasMore ? skip + take : null));
    }

    private static async Task<IResult> GetAsync(Guid communityId, Guid channelId, Guid postId,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsForumVisibleAsync(communityId, channelId, session.AccountId, db, authorization))
            return Results.NotFound();
        var post = await db.CommunityForumPosts.AsNoTracking().Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage)
            .SingleOrDefaultAsync(value => value.Id == postId && value.CommunityId == communityId &&
                value.ForumChannelId == channelId);
        if (post is null) return Results.NotFound();
        var unread = await UnreadCountsAsync([post], session.AccountId, db);
        return Results.Ok(await ToDtoAsync(post, db, unread.GetValueOrDefault(post.Id)));
    }

    private static async Task<IResult> CreateAsync(Guid communityId, Guid channelId,
        CreateCommunityForumPostRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, ICommunityLimitsService limits, IOptions<NodeOptions> nodeOptions,
        IHubContext<ChatHub> hub, HistoricalAuthorPresentationService historicalAuthors)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        var forum = await db.CommunityChannels.SingleOrDefaultAsync(value => value.Id == channelId &&
            value.CommunityId == communityId && value.Kind == CommunityChannelKind.Forum &&
            value.ParentForumChannelId == null);
        if (forum is null) return Results.NotFound();
        if (!access.Has(CommunityPermission.ViewChannels) || !access.Has(CommunityPermission.SendMessages) ||
            !access.Has(CommunityPermission.CreateForumPosts)) return Forbidden();
        var title = request.Title.Trim();
        if (title.Length is < 1 or > MaximumTitleLength)
            return Invalid($"Post titles must contain 1 to {MaximumTitleLength} characters.");
        var requestedTags = request.TagIds?.ToArray() ?? [];
        var tagValidation = await ValidateTagSelectionAsync(channelId, requestedTags, access, db,
            requireAtLeastOne: forum.RequireTag);
        if (tagValidation.Error is { } tagError) return Invalid(tagError);
        var embedValidation = ValidateEmbedChange(request.Embed, forum.AllowDocumentEmbeds,
            access.Has(CommunityPermission.EmbedDocumentsInForumPosts));
        if (embedValidation.Error is { } embedError) return Invalid(embedError);

        var attachmentsResult = await ValidateAttachmentsAsync(request.InitialMessage.AttachmentIds,
            session.AccountId, db, nodeOptions.Value);
        if (attachmentsResult.Error is { } attachmentError) return Invalid(attachmentError);
        if (attachmentsResult.Attachments.Count > 0 && !access.Has(CommunityPermission.AttachFiles))
            return Forbidden();
        var content = request.InitialMessage.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content) && attachmentsResult.Attachments.Count == 0)
            return Invalid("The initial post message cannot be empty.");
        var maximum = limits.GetEffectiveLimits(communityId).MaxMessageCharacters;
        if (MessageText.CountCharacters(content) > maximum)
            return Invalid($"Messages cannot exceed {maximum:N0} characters.");
        var mentionResult = await ValidateMentionsAsync(communityId, channelId, session.AccountId, content,
            request.InitialMessage.Mentions, access, db, authorization);
        if (mentionResult.Error is { } mentionError) return Invalid(mentionError);

        var now = DateTimeOffset.UtcNow;
        var postId = Guid.NewGuid();
        var discussionId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var discussion = new CommunityChannel
        {
            Id = discussionId, CommunityId = communityId, ParentForumChannelId = channelId,
            Name = $"forum-{postId:N}", Kind = CommunityChannelKind.Text, Position = 0,
            CreatedAt = now, Community = null!
        };
        var root = new ChannelMessage
        {
            Id = rootId, CommunityId = communityId, ChannelId = discussionId,
            AuthorAccountId = session.AccountId, ClientMessageId = request.InitialMessage.ClientMessageId,
            Content = content, CreatedAt = now, MentionsJson = mentionResult.Mentions.Count == 0
                ? null : JsonSerializer.Serialize(mentionResult.Mentions),
            AuthorAccount = session.Account, Channel = discussion
        };
        await historicalAuthors.CaptureAsync(root, communityId, session.AccountId);
        foreach (var attachment in attachmentsResult.Attachments)
        {
            attachment.ChannelMessageId = root.Id;
            attachment.ChannelMessage = root;
            root.Attachments.Add(attachment);
        }
        var post = new CommunityForumPost
        {
            Id = postId, CommunityId = communityId, ForumChannelId = channelId,
            DiscussionChannelId = discussionId, RootMessageId = rootId, AuthorAccountId = session.AccountId,
            Title = title, CreatedAt = now, UpdatedAt = now, LastActivityAt = now,
            EmbedProvider = embedValidation.Provider, EmbedUrl = embedValidation.Url,
            Community = null!, ForumChannel = null!, DiscussionChannel = discussion,
            RootMessage = root, AuthorAccount = session.Account
        };
        db.CommunityChannels.Add(discussion);
        db.ChannelMessages.Add(root);
        db.CommunityForumPosts.Add(post);
        foreach (var tag in tagValidation.Tags)
            db.CommunityForumPostTags.Add(new() { Post = post, PostId = post.Id, Tag = tag, TagId = tag.Id });
        foreach (var recipientId in mentionResult.Recipients)
            db.CommunityMentionNotifications.Add(new CommunityMentionNotification
            {
                MessageId = root.Id, AccountId = recipientId, CommunityId = communityId,
                ChannelId = discussionId, CreatedAt = now, Message = root, Account = null!
            });
        await db.SaveChangesAsync();
        var dto = ToDto(post, tags: tagValidation.Tags.Select(ToDto).ToArray());
        await PublishAsync(communityId, channelId, new(communityId, channelId, dto, post.Id, "created", session.AccountId),
            db, authorization, hub);
        return Results.Created($"/api/communities/{communityId}/forums/{channelId}/posts/{post.Id}", dto);
    }

    private static async Task<IResult> UpdateAsync(Guid communityId, Guid channelId, Guid postId,
        UpdateCommunityForumPostRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var post = await db.CommunityForumPosts.Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage)
            .SingleOrDefaultAsync(value => value.Id == postId && value.CommunityId == communityId &&
                value.ForumChannelId == channelId);
        if (post is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        var moderates = access.Has(CommunityPermission.ManageMessages);
        if (request.Title is not null)
        {
            if (post.AuthorAccountId != session.AccountId && !moderates) return Forbidden();
            var title = request.Title.Trim();
            if (title.Length is < 1 or > MaximumTitleLength)
                return Invalid($"Post titles must contain 1 to {MaximumTitleLength} characters.");
            post.Title = title;
        }
        if ((request.IsLocked.HasValue || request.IsPinned.HasValue) && !moderates) return Forbidden();
        if (request.IsLocked.HasValue) post.IsLocked = request.IsLocked.Value;
        if (request.IsPinned.HasValue) post.IsPinned = request.IsPinned.Value;
        if (request.Embed is not null)
        {
            var forum = await db.CommunityChannels.AsNoTracking().SingleAsync(value => value.Id == channelId &&
                value.CommunityId == communityId);
            var mayEditOwn = post.AuthorAccountId == session.AccountId && forum.AllowDocumentEmbeds &&
                             access.Has(CommunityPermission.EmbedDocumentsInForumPosts);
            if (!moderates && !mayEditOwn) return Forbidden();
            var removesEmbed = request.Embed is { Provider: null, Url: null };
            var embedValidation = ValidateEmbedChange(request.Embed, forum.AllowDocumentEmbeds || removesEmbed,
                mayEditOwn || moderates);
            if (embedValidation.Error is { } embedError) return Invalid(embedError);
            post.EmbedProvider = embedValidation.Provider;
            post.EmbedUrl = embedValidation.Url;
        }
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dto = await ToDtoAsync(post, db);
        await PublishAsync(communityId, channelId, new(communityId, channelId, dto, post.Id, "updated"),
            db, authorization, hub);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteAsync(Guid communityId, Guid channelId, Guid postId,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var post = await db.CommunityForumPosts.SingleOrDefaultAsync(value => value.Id == postId &&
            value.CommunityId == communityId && value.ForumChannelId == channelId);
        if (post is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        if (post.AuthorAccountId != session.AccountId && !access.Has(CommunityPermission.ManageMessages))
            return Forbidden();
        var discussion = await db.CommunityChannels.SingleAsync(value => value.Id == post.DiscussionChannelId);
        db.CommunityForumPosts.Remove(post);
        await db.SaveChangesAsync();
        db.CommunityChannels.Remove(discussion);
        await db.SaveChangesAsync();
        await PublishAsync(communityId, channelId, new(communityId, channelId, null, postId, "deleted"),
            db, authorization, hub);
        return Results.NoContent();
    }

    private static async Task<bool> IsForumVisibleAsync(Guid communityId, Guid channelId, Guid accountId,
        IridiumDbContext db, CommunityAuthorizationService authorization) =>
        await db.CommunityChannels.AnyAsync(value => value.Id == channelId && value.CommunityId == communityId &&
            value.Kind == CommunityChannelKind.Forum && value.ParentForumChannelId == null) &&
        await authorization.HasChannelPermissionAsync(communityId, channelId, accountId,
            CommunityPermission.ViewChannels, db);

    private static async Task<Dictionary<Guid, int>> UnreadCountsAsync(IReadOnlyList<CommunityForumPost> posts,
        Guid accountId, IridiumDbContext db)
    {
        if (posts.Count == 0) return [];
        var channelIds = posts.Select(value => value.DiscussionChannelId).ToArray();
        var counts = await db.ChannelMessages.AsNoTracking().Where(message => channelIds.Contains(message.ChannelId) &&
                message.AuthorAccountId != accountId && !message.IsDeleted &&
                !db.CommunityChannelReadStates.Any(state => state.CommunityId == message.CommunityId &&
                    state.ChannelId == message.ChannelId && state.AccountId == accountId &&
                    state.LastReadAt >= message.CreatedAt))
            .GroupBy(value => value.ChannelId)
            .Select(value => new { ChannelId = value.Key, Count = value.Count() })
            .ToDictionaryAsync(value => value.ChannelId, value => value.Count);
        return posts.ToDictionary(value => value.Id, value => counts.GetValueOrDefault(value.DiscussionChannelId));
    }

    internal static CommunityForumPostDto ToDto(CommunityForumPost value, int unreadCount = 0,
        IReadOnlyList<CommunityForumTagDto>? tags = null, bool includeEmbedUrl = true) => new(
        value.Id, value.CommunityId, value.ForumChannelId, value.DiscussionChannelId, value.RootMessageId,
        new(value.AuthorAccountId, value.AuthorAccount.Username,
            value.RootMessage.AuthorDisplayNameSnapshot ?? value.AuthorAccount.DisplayName,
            AvatarRevision: value.RootMessage.AuthorAvatarRevisionSnapshot ?? value.AuthorAccount.AvatarRevision,
            AvatarSnapshotMessageId: value.RootMessage.AuthorAvatarObjectKeySnapshot is null
                ? null : value.RootMessage.Id,
            HasHistoricalSnapshot: value.RootMessage.AuthorDisplayNameSnapshot is not null), value.Title,
        value.CreatedAt, value.UpdatedAt, value.LastActivityAt, value.ReplyCount, value.IsLocked, value.IsPinned,
        unreadCount, RootPreview(value.RootMessage?.Content),
        ChannelMessageMapper.DeserializeMentions(value.RootMessage?.MentionsJson), tags ?? [],
        value.EmbedProvider, includeEmbedUrl ? value.EmbedUrl : null);

    internal static async Task<CommunityForumPostDto> ToDtoAsync(CommunityForumPost value, IridiumDbContext db,
        int unreadCount = 0)
    {
        var map = await LoadPostTagsAsync([value.Id], db);
        return ToDto(value, unreadCount, map.GetValueOrDefault(value.Id));
    }

    internal static CommunityForumTagDto ToDto(CommunityForumTag value) => new(value.Id, value.ChannelId,
        value.Name, value.EmojiKind, value.StandardEmoji, value.CustomEmojiId,
        value.CustomEmojiId is null || value.CustomEmoji is not null, value.Moderated, value.SortOrder, value.CreatedAt);

    private static async Task<Dictionary<Guid, IReadOnlyList<CommunityForumTagDto>>> LoadPostTagsAsync(
        IReadOnlyList<Guid> postIds, IridiumDbContext db)
    {
        if (postIds.Count == 0) return [];
        var rows = await db.CommunityForumPostTags.AsNoTracking().Where(value => postIds.Contains(value.PostId))
            .Include(value => value.Tag).ThenInclude(value => value.CustomEmoji)
            .OrderBy(value => value.Tag.SortOrder).ThenBy(value => value.Tag.Name).ToListAsync();
        return rows.GroupBy(value => value.PostId).ToDictionary(value => value.Key,
            value => (IReadOnlyList<CommunityForumTagDto>)value.Select(row => ToDto(row.Tag)).ToArray());
    }

    internal static async Task<(IReadOnlyList<CommunityForumTag> Tags, string? Error)> ValidateTagSelectionAsync(
        Guid channelId, IReadOnlyList<Guid> requested, CommunityAccessDto access, IridiumDbContext db,
        bool requireAtLeastOne)
    {
        if (requested.Count > CommunityForumTagLimits.MaximumTagsPerPost)
            return ([], $"A Post may have at most {CommunityForumTagLimits.MaximumTagsPerPost} tags.");
        if (requested.Count != requested.Distinct().Count()) return ([], "A tag was selected more than once.");
        if (requireAtLeastOne && requested.Count == 0) return ([], "Select at least one tag before publishing this Post.");
        var values = await db.CommunityForumTags.Include(value => value.CustomEmoji)
            .Where(value => requested.Contains(value.Id)).ToListAsync();
        if (values.Count != requested.Count || values.Any(value => value.ChannelId != channelId))
            return ([], "One or more tags do not belong to this Forum.");
        if (!access.Has(CommunityPermission.ManageMessages) && values.Any(value => value.Moderated))
            return ([], "Only Forum moderators may apply moderated tags.");
        return (values.OrderBy(value => value.SortOrder).ToArray(), null);
    }

    private static IReadOnlyList<Guid>? ParseTagIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<Guid>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Guid.TryParse(item, out var id)) return null; else if (!result.Contains(id)) result.Add(id);
        return result;
    }

    private static string? RootPreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        const int maximumSourceCharacters = 320;
        return content.Length <= maximumSourceCharacters ? content : content[..maximumSourceCharacters];
    }

    private static (CommunityChannelEmbedProvider? Provider, string? Url, string? Error) ValidateEmbedChange(
        CommunityChannelEmbedUpdate? embed, bool forumAllows, bool permitted)
    {
        if (embed is null || embed is { Provider: null, Url: null }) return (null, null, null);
        if (!forumAllows) return (null, null, "This Forum does not allow document embeds.");
        if (!permitted) return (null, null, "You do not have permission to embed documents in Forum Posts.");
        if (embed.Provider != CommunityChannelEmbedProvider.GoogleDocs ||
            !CommunityChannelEmbeds.TryGoogleDocs(embed.Url, out var configuration))
            return (null, null, "Enter a valid Google Docs document URL.");
        return (CommunityChannelEmbedProvider.GoogleDocs,
            configuration!.CanonicalUrl ?? configuration.OpenUrl, null);
    }

    internal static async Task PublishAsync(Guid communityId, Guid channelId, CommunityForumPostChangedEvent change,
        IridiumDbContext db, CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var recipients = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == communityId)
            .Select(value => value.AccountId).ToListAsync();
        var owner = await db.Communities.AsNoTracking().Where(value => value.Id == communityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync();
        if (owner.HasValue) recipients.Add(owner.Value);
        foreach (var accountId in recipients.Distinct())
            if (await authorization.HasChannelPermissionAsync(communityId, channelId, accountId,
                    CommunityPermission.ViewChannels, db))
            {
                await hub.Clients.Group(ChatHub.AccountGroup(accountId)).SendAsync(CommunityForumHubContract.PostChanged, change);
                if (change.Change == "created" && accountId != change.ActorAccountId)
                    await hub.Clients.Group(ChatHub.AccountGroup(accountId)).SendAsync(CommunityHubContract.ChannelActivity,
                        new CommunityChannelActivityEvent(communityId, channelId, change.ActorAccountId ?? Guid.Empty));
            }
    }

    private static async Task<(List<Attachment> Attachments, string? Error)> ValidateAttachmentsAsync(
        IReadOnlyList<Guid>? requested, Guid accountId, IridiumDbContext db, NodeOptions options)
    {
        if (requested is null || requested.Count == 0) return ([], null);
        if (requested.Count > options.MaxAttachmentsPerMessage)
            return ([], $"Messages may contain at most {options.MaxAttachmentsPerMessage} attachments.");
        if (requested.Count != requested.Distinct().Count()) return ([], "An attachment was selected more than once.");
        var attachments = await db.Attachments.Where(value => requested.Contains(value.Id)).ToListAsync();
        if (attachments.Count != requested.Count || attachments.Any(value => value.UploaderAccountId != accountId))
            return ([], "One or more attachments are unavailable.");
        if (attachments.Any(value => value.ChannelMessageId != null || value.DirectMessageId != null))
            return ([], "One or more attachments have already been sent.");
        if (attachments.Any(value => value.OriginalSizeBytes > options.MaxAttachmentBytes))
            return ([], "One or more attachments exceed this Node's file limit.");
        return (requested.Select(id => attachments.Single(value => value.Id == id)).ToList(), null);
    }

    private static async Task<(List<CommunityMentionDto> Mentions, HashSet<Guid> Recipients, string? Error)>
        ValidateMentionsAsync(Guid communityId, Guid channelId, Guid senderId, string content,
            IReadOnlyList<CommunityMentionInput>? requested, CommunityAccessDto access, IridiumDbContext db,
            CommunityAuthorizationService authorization)
    {
        if (requested is null || requested.Count == 0) return ([], [], null);
        if (requested.Count > 16) return ([], [], "A message cannot contain more than 16 mention targets.");
        var memberIds = await db.CommunityMembers.Where(value => value.CommunityId == communityId)
            .Select(value => value.AccountId).ToListAsync();
        var roles = await db.CommunityRoles.Where(value => value.CommunityId == communityId).ToListAsync();
        var result = new List<CommunityMentionDto>();
        var recipients = new HashSet<Guid>();
        var unique = new HashSet<(CommunityMentionKind, Guid?, int)>();
        foreach (var input in requested.OrderBy(value => value.Start))
        {
            if (input.Start < 0 || input.Length < 2 || input.Start + input.Length > content.Length ||
                content[input.Start] != '@') return ([], [], "A mention does not match the message content.");
            if (!MessageText.AllowsMentionAt(content, input.Start) || !unique.Add((input.Kind, input.TargetId, input.Start)))
                continue;
            switch (input.Kind)
            {
                case CommunityMentionKind.Account when input.TargetId is { } accountId:
                    if (!memberIds.Contains(accountId)) return ([], [], "Mentioned account is not a member of this Server.");
                    var account = await db.Accounts.AsNoTracking().SingleAsync(value => value.Id == accountId);
                    result.Add(new(input.Kind, accountId, input.Start, input.Length, $"@{account.DisplayName}"));
                    if (accountId != senderId && await authorization.HasChannelPermissionAsync(communityId, channelId,
                            accountId, CommunityPermission.ViewChannels, db)) recipients.Add(accountId);
                    break;
                case CommunityMentionKind.Role when input.TargetId is { } roleId:
                    var role = roles.SingleOrDefault(value => value.Id == roleId);
                    if (role is null) return ([], [], "Mentioned role does not belong to this Server.");
                    if (!role.IsMentionable && !access.Has(CommunityPermission.MentionEveryone))
                        return ([], [], "You do not have permission to mention that role.");
                    result.Add(new(input.Kind, roleId, input.Start, input.Length, $"@{role.Name.TrimStart('@')}"));
                    var roleMembers = await db.CommunityMemberRoles.Where(value => value.CommunityId == communityId &&
                        value.RoleId == roleId && value.AccountId != senderId).Select(value => value.AccountId).ToListAsync();
                    foreach (var id in roleMembers)
                        if (await authorization.HasChannelPermissionAsync(communityId, channelId, id,
                                CommunityPermission.ViewChannels, db)) recipients.Add(id);
                    break;
                case CommunityMentionKind.Everyone:
                    if (!access.Has(CommunityPermission.MentionEveryone))
                        return ([], [], "You do not have permission to mention everyone.");
                    result.Add(new(input.Kind, null, input.Start, input.Length, "@everyone"));
                    foreach (var id in memberIds.Where(value => value != senderId))
                        if (await authorization.HasChannelPermissionAsync(communityId, channelId, id,
                                CommunityPermission.ViewChannels, db)) recipients.Add(id);
                    break;
                default:
                    return ([], [], "That mention target is invalid.");
            }
        }
        return (result, recipients, null);
    }

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["forum"] = [message]
    });

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);
}
