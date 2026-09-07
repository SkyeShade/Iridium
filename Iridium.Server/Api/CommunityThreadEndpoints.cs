using Iridium.Protocol;
using Iridium.Server.Communities;
using Iridium.Server.Domain;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class CommunityThreadEndpoints
{
    private const int DefaultPageSize = 30;
    private const int MaximumPageSize = 50;
    public const int SidebarThreadLimit = 15;

    public static IEndpointRouteBuilder MapCommunityThreadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/communities/{communityId:guid}/thread-memberships", ListJoinedAsync);
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}/channels/{channelId:guid}/threads");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{threadId:guid}", GetAsync);
        group.MapPatch("/{threadId:guid}", UpdateAsync);
        group.MapDelete("/{threadId:guid}", DeleteAsync);
        group.MapPut("/{threadId:guid}/membership", JoinAsync);
        group.MapDelete("/{threadId:guid}/membership", LeaveAsync);
        group.MapPut("/{threadId:guid}/notification-settings", UpdateNotificationAsync);
        group.MapGet("/{threadId:guid}/members", ListMembersAsync);
        group.MapPut("/{threadId:guid}/members/{accountId:guid}", AddMemberAsync);
        group.MapDelete("/{threadId:guid}/members/{accountId:guid}", RemoveMemberAsync);
        return endpoints;
    }

    private static async Task<IResult> ListJoinedAsync(Guid communityId, int? limit, HttpContext context,
        IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        await ArchiveExpiredAsync(db);
        var take = Math.Clamp(limit ?? SidebarThreadLimit, 1, 100);
        var rows = await db.CommunityThreadMembers.AsNoTracking()
            .Where(value => value.AccountId == session.AccountId && value.Thread.CommunityId == communityId &&
                            !value.Thread.IsArchived)
            .Include(value => value.Thread).OrderByDescending(value => value.Thread.LastActivityAt)
            .Take(take + 1).ToListAsync();
        var visibleParents = await authorization.VisibleChannelIdsAsync(communityId,
            rows.Select(value => value.Thread.ParentChannelId).Distinct().ToArray(), session.AccountId,
            CommunityPermission.ViewChannels, db);
        var visible = rows.Where(value => visibleParents.Contains(value.Thread.ParentChannelId)).ToList();
        var hasMore = visible.Count > take;
        return Results.Ok(new CommunityThreadPageDto((await ToDtosAsync(visible.Take(take).Select(value => value.Thread)
            .ToArray(), session.AccountId, db, authorization)).ToArray(), hasMore));
    }

    private static async Task<IResult> ListAsync(Guid communityId, Guid channelId, CommunityThreadListKind? view,
        int? offset, int? limit, string? search, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await ParentAccessAsync(communityId, channelId, session.AccountId, db, authorization);
        if (access is null) return Results.NotFound();
        await ArchiveExpiredAsync(db, communityId, channelId);
        var skip = Math.Max(0, offset ?? 0);
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaximumPageSize);
        var kind = view ?? CommunityThreadListKind.Active;
        var query = db.CommunityThreads.AsNoTracking().Where(value => value.CommunityId == communityId &&
            value.ParentChannelId == channelId);
        query = kind switch
        {
            CommunityThreadListKind.Archived => query.Where(value => value.IsArchived),
            CommunityThreadListKind.Joined => query.Where(value => value.Members.Any(member =>
                member.AccountId == session.AccountId)),
            _ => query.Where(value => !value.IsArchived)
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(value => EF.Functions.Like(value.Name, $"%{term}%"));
        }
        if (!access.Has(CommunityPermission.ManageThreads)) query = query.Where(value => !value.IsPrivate ||
            value.OwnerAccountId == session.AccountId ||
            value.Members.Any(member => member.AccountId == session.AccountId));
        var values = await query.OrderByDescending(value => value.LastActivityAt).Skip(skip).Take(take + 1)
            .ToListAsync();
        var hasMore = values.Count > take;
        return Results.Ok(new CommunityThreadPageDto((await ToDtosAsync(values.Take(take).ToArray(),
            session.AccountId, db, authorization)).ToArray(), hasMore));
    }

    private static async Task<IResult> GetAsync(Guid communityId, Guid channelId, Guid threadId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        await ArchiveExpiredAsync(db, communityId, channelId);
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null || !(await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
                session.AccountId, db)).Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        return Results.Ok(await ToDtoAsync(thread, session.AccountId, db, authorization));
    }

    private static async Task<IResult> CreateAsync(Guid communityId, Guid channelId,
        CreateCommunityThreadRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime,
        IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var access = await ParentAccessAsync(communityId, channelId, session.AccountId, db, authorization);
        if (access is null) return Results.NotFound();
        var permission = request.IsPrivate ? CommunityPermission.CreatePrivateThreads :
            CommunityPermission.CreatePublicThreads;
        if (!access.Has(permission)) return Forbidden();
        var name = request.Name.Trim();
        if (name.Length is < CommunityThreadLimits.MinimumNameLength or > CommunityThreadLimits.MaximumNameLength)
            return Invalid($"Thread names must be between 1 and {CommunityThreadLimits.MaximumNameLength} characters.");
        if (!Enum.IsDefined(request.AutoArchiveDuration)) return Invalid("That auto-archive duration is invalid.");
        if (await db.CommunityThreads.CountAsync(value => value.CommunityId == communityId &&
                value.ParentChannelId == channelId && !value.IsArchived) >= CommunityThreadLimits.MaximumActiveThreadsPerChannel)
            return Results.Conflict(new { message = "This channel has reached its active Thread limit." });
        var creationWindow = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (await db.CommunityThreads.CountAsync(value => value.CommunityId == communityId &&
                value.OwnerAccountId == session.AccountId && value.CreatedAt >= creationWindow) >=
            CommunityThreadLimits.MaximumCreatesPerMinute)
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        if (request.CreatedFromMessageId is { } rootId)
        {
            if (!await db.ChannelMessages.AnyAsync(value => value.Id == rootId && value.CommunityId == communityId &&
                    value.ChannelId == channelId && !value.IsDeleted)) return Invalid("That message is unavailable.");
            if (await db.CommunityThreads.AnyAsync(value => value.CreatedFromMessageId == rootId))
                return Results.Conflict(new { message = "That message already has a Thread." });
        }
        var parent = await db.CommunityChannels.SingleAsync(value => value.CommunityId == communityId &&
            value.Id == channelId);
        var now = DateTimeOffset.UtcNow;
        var discussion = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = communityId, CategoryId = parent.CategoryId,
            ParentThreadChannelId = channelId, Name = name, Kind = CommunityChannelKind.Text,
            Position = 0, CreatedAt = now, PermissionsSyncedToCategory = parent.PermissionsSyncedToCategory,
            Community = null!
        };
        var thread = new CommunityThread
        {
            Id = Guid.NewGuid(), CommunityId = communityId, ParentChannelId = channelId,
            DiscussionChannelId = discussion.Id, OwnerAccountId = session.AccountId,
            CreatedFromMessageId = request.CreatedFromMessageId, Name = name, CreatedAt = now,
            IsPrivate = request.IsPrivate, AutoArchiveDuration = request.AutoArchiveDuration,
            LastActivityAt = now, Community = null!, ParentChannel = parent, DiscussionChannel = discussion,
            OwnerAccount = null!
        };
        db.CommunityChannels.Add(discussion);
        db.CommunityThreads.Add(thread);
        db.CommunityThreadMembers.Add(new CommunityThreadMember
        {
            ThreadId = thread.Id, AccountId = session.AccountId, JoinedAt = now,
            NotificationLevel = ThreadNotificationLevel.MentionsOnly, Thread = thread, Account = null!
        });
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "thread-created", db);
        await PublishAsync(thread, CommunityThreadHubContract.Created, db, authorization, hub);
        return Results.Created($"/api/communities/{communityId}/channels/{channelId}/threads/{thread.Id}",
            await ToDtoAsync(thread, session.AccountId, db, authorization));
    }

    private static async Task<IResult> UpdateAsync(Guid communityId, Guid channelId, Guid threadId,
        UpdateCommunityThreadRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, CommunityRealtimePublisher realtime, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        var manage = access.Has(CommunityPermission.ManageThreads);
        var own = thread.OwnerAccountId == session.AccountId;
        var participantReopen = request.IsArchived == false && !thread.IsLocked &&
            access.Has(CommunityPermission.SendMessagesInThreads) && request.Name is null &&
            request.IsLocked is null && request.SlowmodeSeconds is null && request.AutoArchiveDuration is null;
        if (!manage && !own && !participantReopen) return Forbidden();
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length is < 1 or > CommunityThreadLimits.MaximumNameLength) return Invalid("Invalid Thread name.");
            thread.Name = name;
            thread.DiscussionChannel.Name = name;
        }
        if (request.IsLocked is not null && !manage) return Forbidden();
        if ((request.SlowmodeSeconds is not null || request.AutoArchiveDuration is not null) && !manage)
            return Forbidden();
        if (request.SlowmodeSeconds is { } slowmode && !CommunityThreadLimits.SlowmodeSeconds.Contains(slowmode))
            return Invalid("That slowmode duration is invalid.");
        if (request.AutoArchiveDuration is { } duration && !Enum.IsDefined(duration))
            return Invalid("That auto-archive duration is invalid.");
        if (request.IsLocked is { } locked)
        {
            thread.IsLocked = locked;
            if (locked) SetArchived(thread, true);
        }
        if (request.IsArchived is { } archived)
        {
            if (!archived && thread.IsLocked && !manage) return Results.Conflict(new { message = "A locked Thread can only be reopened by a moderator." });
            SetArchived(thread, archived);
        }
        if (request.SlowmodeSeconds is { } value) thread.SlowmodeSeconds = value;
        if (request.AutoArchiveDuration is { } archiveDuration) thread.AutoArchiveDuration = archiveDuration;
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "thread-updated", db);
        var method = request.IsArchived switch
        {
            true => CommunityThreadHubContract.Archived,
            false => CommunityThreadHubContract.Unarchived,
            _ => CommunityThreadHubContract.Updated
        };
        await PublishAsync(thread, method, db, authorization, hub);
        return Results.Ok(await ToDtoAsync(thread, session.AccountId, db, authorization));
    }

    private static async Task<IResult> DeleteAsync(Guid communityId, Guid channelId, Guid threadId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        CommunityRealtimePublisher realtime, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        if (!access.Has(CommunityPermission.ManageThreads)) return Forbidden();
        var discussion = thread.DiscussionChannel;
        await PublishAsync(thread, CommunityThreadHubContract.Deleted, db, authorization, hub);
        db.CommunityThreads.Remove(thread);
        await db.SaveChangesAsync();
        db.CommunityChannels.Remove(discussion);
        await db.SaveChangesAsync();
        await realtime.PublishAsync(communityId, "thread-deleted", db);
        return Results.NoContent();
    }

    private static async Task<IResult> JoinAsync(Guid communityId, Guid channelId, Guid threadId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        if (thread.IsLocked && !access.Has(CommunityPermission.ManageThreads)) return Forbidden();
        var member = await db.CommunityThreadMembers.FindAsync(threadId, session.AccountId);
        if (member is null)
            db.CommunityThreadMembers.Add(new CommunityThreadMember
            {
                ThreadId = threadId, AccountId = session.AccountId, JoinedAt = DateTimeOffset.UtcNow,
                NotificationLevel = ThreadNotificationLevel.MentionsOnly, Thread = thread, Account = null!
            });
        if (thread.IsArchived) SetArchived(thread, false);
        await db.SaveChangesAsync();
        await PublishMembershipAsync(thread, session.AccountId, true, hub);
        return Results.Ok(await ToDtoAsync(thread, session.AccountId, db, authorization));
    }

    private static async Task<IResult> LeaveAsync(Guid communityId, Guid channelId, Guid threadId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        var member = await db.CommunityThreadMembers.FindAsync(threadId, session.AccountId);
        if (member is not null) db.CommunityThreadMembers.Remove(member);
        await db.SaveChangesAsync();
        await PublishMembershipAsync(thread, session.AccountId, false, hub);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateNotificationAsync(Guid communityId, Guid channelId, Guid threadId,
        UpdateThreadNotificationRequest request, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!Enum.IsDefined(request.Level)) return Invalid("That notification level is invalid.");
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        var member = await db.CommunityThreadMembers.SingleOrDefaultAsync(value => value.ThreadId == threadId &&
            value.AccountId == session.AccountId);
        if (member is null) return Results.Conflict(new { message = "Join this Thread before changing notifications." });
        member.NotificationLevel = request.Level;
        if (request.Level == ThreadNotificationLevel.Muted)
        {
            var mentions = await db.CommunityMentionNotifications.Where(value => value.AccountId == session.AccountId &&
                value.ChannelId == thread.DiscussionChannelId && value.ReadAt == null).ToListAsync();
            foreach (var mention in mentions) mention.ReadAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        await PublishMembershipAsync(thread, session.AccountId, true, hub);
        return Results.Ok(await ToDtoAsync(thread, session.AccountId, db, authorization));
    }

    private static async Task<IResult> ListMembersAsync(Guid communityId, Guid channelId, Guid threadId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null || !(await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
                session.AccountId, db)).Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        return Results.Ok(await db.CommunityThreadMembers.AsNoTracking().Where(value => value.ThreadId == threadId)
            .OrderBy(value => value.JoinedAt).Select(value => new ThreadMemberDto(value.AccountId,
                value.Account.DisplayName, value.JoinedAt)).ToArrayAsync());
    }

    private static async Task<IResult> AddMemberAsync(Guid communityId, Guid channelId, Guid threadId, Guid accountId,
        HttpContext context, IridiumDbContext db, SessionService sessions, CommunityAuthorizationService authorization,
        IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        if (!thread.IsPrivate || thread.OwnerAccountId != session.AccountId && !access.Has(CommunityPermission.ManageThreads))
            return Forbidden();
        if (!(await authorization.GetChannelAccessAsync(communityId, channelId, accountId, db))
            .Has(CommunityPermission.ViewChannels)) return Invalid("That member cannot view the parent channel.");
        if (!await db.CommunityThreadMembers.AnyAsync(value => value.ThreadId == threadId && value.AccountId == accountId))
            db.CommunityThreadMembers.Add(new CommunityThreadMember
            {
                ThreadId = threadId, AccountId = accountId, JoinedAt = DateTimeOffset.UtcNow,
                NotificationLevel = ThreadNotificationLevel.MentionsOnly, Thread = thread, Account = null!
            });
        await db.SaveChangesAsync();
        await PublishMembershipAsync(thread, accountId, true, hub);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(Guid communityId, Guid channelId, Guid threadId,
        Guid accountId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var thread = await FindAsync(communityId, channelId, threadId, db);
        if (thread is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(communityId, thread.DiscussionChannelId,
            session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();
        if (accountId != session.AccountId && (thread.OwnerAccountId != session.AccountId || !thread.IsPrivate) &&
            !access.Has(CommunityPermission.ManageThreads)) return Forbidden();
        var member = await db.CommunityThreadMembers.FindAsync(threadId, accountId);
        if (member is not null) db.CommunityThreadMembers.Remove(member);
        await db.SaveChangesAsync();
        await PublishMembershipAsync(thread, accountId, false, hub);
        return Results.NoContent();
    }

    public static async Task<IReadOnlySet<Guid>> ArchiveExpiredAsync(IridiumDbContext db, Guid? communityId = null,
        Guid? parentChannelId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.CommunityThreads.Where(value => !value.IsArchived);
        if (communityId is { } community) query = query.Where(value => value.CommunityId == community);
        if (parentChannelId is { } parent) query = query.Where(value => value.ParentChannelId == parent);
        var candidates = await query.ToListAsync();
        var changed = false;
        var changedCommunities = new HashSet<Guid>();
        foreach (var thread in candidates.Where(value => value.LastActivityAt.AddMinutes((int)value.AutoArchiveDuration) <= now))
        {
            SetArchived(thread, true, now);
            changed = true;
            changedCommunities.Add(thread.CommunityId);
        }
        if (changed) await db.SaveChangesAsync();
        return changedCommunities;
    }

    internal static void SetArchived(CommunityThread thread, bool archived, DateTimeOffset? now = null)
    {
        thread.IsArchived = archived;
        thread.ArchivedAt = archived ? now ?? DateTimeOffset.UtcNow : null;
        if (!archived) thread.LastActivityAt = now ?? DateTimeOffset.UtcNow;
    }

    private static async Task<CommunityAccessDto?> ParentAccessAsync(Guid communityId, Guid channelId,
        Guid accountId, IridiumDbContext db, CommunityAuthorizationService authorization)
    {
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId &&
                value.Kind == CommunityChannelKind.Text && value.ParentForumChannelId == null &&
                value.ParentThreadChannelId == null)) return null;
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, accountId, db);
        return access.Has(CommunityPermission.ViewChannels) ? access : null;
    }

    private static Task<CommunityThread?> FindAsync(Guid communityId, Guid channelId, Guid threadId,
        IridiumDbContext db) => db.CommunityThreads.Include(value => value.DiscussionChannel)
        .SingleOrDefaultAsync(value => value.Id == threadId && value.CommunityId == communityId &&
            value.ParentChannelId == channelId);

    private static async Task<IReadOnlyList<CommunityThreadDto>> ToDtosAsync(IReadOnlyList<CommunityThread> threads,
        Guid accountId, IridiumDbContext db, CommunityAuthorizationService authorization)
    {
        if (threads.Count == 0) return [];
        var ids = threads.Select(value => value.Id).ToArray();
        var discussionIds = threads.Select(value => value.DiscussionChannelId).ToArray();
        var members = await db.CommunityThreadMembers.AsNoTracking().Where(value => value.AccountId == accountId &&
            ids.Contains(value.ThreadId)).ToDictionaryAsync(value => value.ThreadId);
        var reads = await db.CommunityChannelReadStates.AsNoTracking().Where(value => value.AccountId == accountId &&
            discussionIds.Contains(value.ChannelId)).ToDictionaryAsync(value => value.ChannelId,
            value => value.LastReadAt);
        var messages = await db.ChannelMessages.AsNoTracking().Where(value => discussionIds.Contains(value.ChannelId) &&
                value.AuthorAccountId != accountId && !value.IsDeleted)
            .Select(value => new { value.ChannelId, value.CreatedAt }).ToListAsync();
        var unread = messages.GroupBy(value => value.ChannelId).ToDictionary(value => value.Key,
            value => value.Count(message => !reads.TryGetValue(message.ChannelId, out var readAt) ||
                                           message.CreatedAt > readAt));
        var mentionCounts = await db.CommunityMentionNotifications.AsNoTracking().Where(value =>
                value.AccountId == accountId && discussionIds.Contains(value.ChannelId) && value.ReadAt == null)
            .GroupBy(value => value.ChannelId).Select(value => new { ChannelId = value.Key, Count = value.Count() })
            .ToDictionaryAsync(value => value.ChannelId, value => value.Count);
        var accessByParent = new Dictionary<Guid, CommunityAccessDto>();
        foreach (var parentId in threads.Select(value => value.ParentChannelId).Distinct())
            accessByParent[parentId] = await authorization.GetChannelAccessAsync(threads[0].CommunityId, parentId,
                accountId, db);
        return threads.Select(value =>
        {
            members.TryGetValue(value.Id, out var member);
            var mentions = member?.NotificationLevel == ThreadNotificationLevel.Muted ? 0 :
                mentionCounts.GetValueOrDefault(value.DiscussionChannelId);
            var access = accessByParent[value.ParentChannelId];
            return new CommunityThreadDto(value.Id, value.CommunityId, value.ParentChannelId,
                value.DiscussionChannelId, value.Name, value.OwnerAccountId, value.CreatedFromMessageId,
                value.CreatedAt, value.ArchivedAt, value.IsArchived, value.IsLocked, value.IsPrivate,
                value.AutoArchiveDuration, value.SlowmodeSeconds, value.LastActivityAt, value.ReplyCount,
                member is not null, member?.NotificationLevel, member?.JoinedAt,
                unread.GetValueOrDefault(value.DiscussionChannelId), mentions,
                access.Has(CommunityPermission.ManageThreads), value.OwnerAccountId == accountId);
        }).ToArray();
    }

    internal static async Task<CommunityThreadDto> ToDtoAsync(CommunityThread value, Guid accountId,
        IridiumDbContext db, CommunityAuthorizationService authorization)
    {
        var member = await db.CommunityThreadMembers.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ThreadId == value.Id && row.AccountId == accountId);
        var access = await authorization.GetChannelAccessAsync(value.CommunityId, value.DiscussionChannelId,
            accountId, db);
        var unread = await db.ChannelMessages.AsNoTracking().CountAsync(message =>
            message.ChannelId == value.DiscussionChannelId && message.AuthorAccountId != accountId &&
            !message.IsDeleted && !db.CommunityChannelReadStates.Any(state => state.CommunityId == value.CommunityId &&
                state.ChannelId == value.DiscussionChannelId && state.AccountId == accountId &&
                state.LastReadAt >= message.CreatedAt));
        var mentions = member?.NotificationLevel == ThreadNotificationLevel.Muted ? 0 :
            await db.CommunityMentionNotifications.AsNoTracking().CountAsync(notification =>
                notification.ChannelId == value.DiscussionChannelId && notification.AccountId == accountId &&
                notification.ReadAt == null);
        return new(value.Id, value.CommunityId, value.ParentChannelId, value.DiscussionChannelId, value.Name,
            value.OwnerAccountId, value.CreatedFromMessageId, value.CreatedAt, value.ArchivedAt, value.IsArchived,
            value.IsLocked, value.IsPrivate, value.AutoArchiveDuration, value.SlowmodeSeconds, value.LastActivityAt,
            value.ReplyCount, member is not null, member?.NotificationLevel, member?.JoinedAt, unread, mentions,
            access.Has(CommunityPermission.ManageThreads), value.OwnerAccountId == accountId);
    }

    private static async Task PublishAsync(CommunityThread thread, string method, IridiumDbContext db,
        CommunityAuthorizationService authorization, IHubContext<ChatHub> hub)
    {
        var recipients = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == thread.CommunityId)
            .Select(value => value.AccountId).ToListAsync();
        var owner = await db.Communities.AsNoTracking().Where(value => value.Id == thread.CommunityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync();
        if (owner is { } ownerId) recipients.Add(ownerId);
        var payload = new CommunityThreadChangedEvent(thread.CommunityId, thread.ParentChannelId, thread.Id,
            method == CommunityThreadHubContract.Deleted);
        foreach (var accountId in recipients.Distinct())
            if ((await authorization.GetChannelAccessAsync(thread.CommunityId, thread.DiscussionChannelId,
                    accountId, db)).Has(CommunityPermission.ViewChannels))
                await hub.Clients.Group(ChatHub.AccountGroup(accountId)).SendAsync(method, payload);
    }

    private static Task PublishMembershipAsync(CommunityThread thread, Guid accountId, bool joined,
        IHubContext<ChatHub> hub) => hub.Clients.Group(ChatHub.AccountGroup(accountId)).SendAsync(
        CommunityThreadHubContract.MembershipChanged, new CommunityThreadMembershipChangedEvent(thread.CommunityId,
            thread.ParentChannelId, thread.Id, accountId, joined));

    private static IResult Invalid(string message) => Results.BadRequest(new { message });
    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);
}
