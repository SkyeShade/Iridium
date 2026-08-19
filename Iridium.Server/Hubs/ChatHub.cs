using Iridium.Protocol;
using Iridium.Server.Api;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Iridium.Server.Hubs;

public sealed class ChatHub(
    ConnectionCounter connections,
    PresenceTracker presence,
    IridiumDbContext db,
    SessionService sessions,
    CommunityAuthorizationService authorization) : Hub
{
    private const string CountedKey = "iridium.connection-counted";
    private const string AccountKey = "iridium.account-id";

    public override async Task OnConnectedAsync()
    {
        var session = await GetSessionAsync();
        if (session is null)
        {
            Context.Abort();
            return;
        }

        Context.Items[CountedKey] = true;
        Context.Items[AccountKey] = session.AccountId;
        connections.Connected();
        await Groups.AddToGroupAsync(Context.ConnectionId, AccountGroup(session.AccountId));
        await BroadcastPresenceAsync(session.AccountId,
            presence.Connected(session.AccountId, session.Account.PreferredPresence));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.ContainsKey(CountedKey)) connections.Disconnected();
        if (Context.Items.TryGetValue(AccountKey, out var value) && value is Guid accountId)
            await BroadcastPresenceAsync(accountId, presence.Disconnected(accountId));
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SetPresence(UserPresence preferred)
    {
        if (!Enum.IsDefined(preferred)) throw new HubException("That presence is not supported.");
        var session = await RequireSessionAsync();
        session.Account.PreferredPresence = preferred;
        await db.SaveChangesAsync();
        await BroadcastPresenceAsync(session.AccountId, presence.SetPreferred(session.AccountId, preferred));
    }

    public async Task JoinChannel(Guid communityId, Guid channelId)
    {
        var accountId = await RequireAccountAsync();
        await RequireChannelAsync(communityId, channelId, accountId, CommunityPermission.ViewChannels);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(communityId, channelId));
    }

    public Task LeaveChannel(Guid communityId, Guid channelId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(communityId, channelId));

    public async Task<ChannelMessageDto> SendMessage(
        Guid communityId,
        Guid channelId,
        SendChannelMessageRequest request)
    {
        var session = await RequireSessionAsync();
        await RequireChannelAsync(communityId, channelId, session.AccountId, CommunityPermission.SendMessages);
        var content = ValidContent(request.Content);
        var (mentions, recipients) = await ValidateMentionsAsync(
            communityId, session.AccountId, content, request.Mentions);

        ChannelMessage? reply = null;
        if (request.ReplyToMessageId is { } replyId)
        {
            reply = await db.ChannelMessages.Include(value => value.AuthorAccount)
                .SingleOrDefaultAsync(value => value.Id == replyId && value.CommunityId == communityId && value.ChannelId == channelId);
            if (reply is null || reply.IsDeleted) throw new HubException("The message being replied to is no longer available.");
        }

        var message = new ChannelMessage
        {
            Id = Guid.NewGuid(),
            CommunityId = communityId,
            ChannelId = channelId,
            AuthorAccountId = session.AccountId,
            AuthorAccount = session.Account,
            Channel = null!,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            ReplyToMessageId = reply?.Id,
            ReplyToMessage = reply,
            MentionsJson = mentions.Count == 0 ? null : JsonSerializer.Serialize(mentions)
        };
        db.ChannelMessages.Add(message);
        await db.SaveChangesAsync();
        var result = ChannelMessageMapper.ToDto(message);
        await Clients.Group(GroupName(communityId, channelId)).SendAsync(ChatHubContract.MessageCreated, result);
        if (recipients.Count > 0)
        {
            var mentionEvent = new CommunityMentionReceivedEvent(communityId, channelId, message.Id, session.AccountId);
            await Clients.Groups(recipients.Select(AccountGroup).ToArray())
                .SendAsync(CommunityMentionHubContract.Received, mentionEvent);
        }
        return result;
    }

    public async Task<ChannelMessageDto> EditMessage(
        Guid communityId,
        Guid channelId,
        Guid messageId,
        EditChannelMessageRequest request)
    {
        var accountId = await RequireAccountAsync();
        await RequireChannelAsync(communityId, channelId, accountId, CommunityPermission.ViewChannels);
        var message = await MessageInContextAsync(communityId, channelId, messageId);
        if (message.AuthorAccountId != accountId) throw new HubException("You can only edit your own messages.");
        if (message.IsDeleted) throw new HubException("Deleted messages cannot be edited.");

        message.Content = ValidContent(request.Content);
        message.MentionsJson = null;
        message.EditedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var result = ChannelMessageMapper.ToDto(message);
        await Clients.Group(GroupName(communityId, channelId)).SendAsync(ChatHubContract.MessageUpdated, result);
        return result;
    }

    public async Task DeleteMessage(Guid communityId, Guid channelId, Guid messageId)
    {
        var accountId = await RequireAccountAsync();
        await RequireChannelAsync(communityId, channelId, accountId, CommunityPermission.ViewChannels);
        var message = await MessageInContextAsync(communityId, channelId, messageId);
        var mayModerate = await authorization.HasPermissionAsync(
            communityId, accountId, CommunityPermission.ManageMessages, db);
        if (message.AuthorAccountId != accountId && !mayModerate)
            throw new HubException("You do not have permission to delete this message.");
        if (message.IsDeleted) return;

        message.IsDeleted = true;
        message.Content = string.Empty;
        message.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await Clients.Group(GroupName(communityId, channelId)).SendAsync(
            ChatHubContract.MessageDeleted,
            new ChannelMessageDeletedEvent(communityId, channelId, messageId, message.DeletedAt.Value));
    }

    public async Task JoinDirectConversation(Guid conversationId)
    {
        var accountId = await RequireAccountAsync();
        await RequireDirectConversationAsync(conversationId, accountId);
        await Groups.AddToGroupAsync(Context.ConnectionId, DirectGroup(conversationId));
    }

    public Task LeaveDirectConversation(Guid conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DirectGroup(conversationId));

    public async Task<DirectMessageDto> SendDirectMessage(Guid conversationId, SendDirectMessageRequest request)
    {
        var session = await RequireSessionAsync();
        var conversation = await RequireDirectConversationAsync(conversationId, session.AccountId);
        DirectMessage? reply = null;
        if (request.ReplyToMessageId is { } replyId)
        {
            reply = await db.DirectMessages.Include(value => value.AuthorAccount)
                .SingleOrDefaultAsync(value => value.Id == replyId && value.ConversationId == conversationId);
            if (reply is null || reply.IsDeleted) throw new HubException("The message being replied to is no longer available.");
        }
        var message = new DirectMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Conversation = conversation,
            AuthorAccountId = session.AccountId,
            AuthorAccount = session.Account,
            Content = ValidContent(request.Content),
            CreatedAt = DateTimeOffset.UtcNow,
            ReplyToMessageId = reply?.Id,
            ReplyToMessage = reply
        };
        db.DirectMessages.Add(message);
        await db.SaveChangesAsync();
        var result = DirectMessageMapper.ToDto(message);
        await DirectParticipants(conversation).SendAsync(DirectMessageHubContract.MessageCreated, result);
        return result;
    }

    public async Task<DirectMessageDto> EditDirectMessage(
        Guid conversationId,
        Guid messageId,
        EditDirectMessageRequest request)
    {
        var accountId = await RequireAccountAsync();
        var conversation = await RequireDirectConversationAsync(conversationId, accountId);
        var message = await DirectMessageInContextAsync(conversationId, messageId);
        if (message.AuthorAccountId != accountId) throw new HubException("You can only edit your own messages.");
        if (message.IsDeleted) throw new HubException("Deleted messages cannot be edited.");
        message.Content = ValidContent(request.Content);
        message.EditedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var result = DirectMessageMapper.ToDto(message);
        await DirectParticipants(conversation).SendAsync(DirectMessageHubContract.MessageUpdated, result);
        return result;
    }

    public async Task DeleteDirectMessage(Guid conversationId, Guid messageId)
    {
        var accountId = await RequireAccountAsync();
        var conversation = await RequireDirectConversationAsync(conversationId, accountId);
        var message = await DirectMessageInContextAsync(conversationId, messageId);
        if (message.AuthorAccountId != accountId) throw new HubException("You can only delete your own messages.");
        if (message.IsDeleted) return;
        message.IsDeleted = true;
        message.Content = string.Empty;
        message.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await DirectParticipants(conversation).SendAsync(
            DirectMessageHubContract.MessageDeleted,
            new DirectMessageDeletedEvent(conversationId, messageId, message.DeletedAt.Value));
    }

    private async Task<ChannelMessage> MessageInContextAsync(Guid communityId, Guid channelId, Guid messageId)
    {
        var message = await db.ChannelMessages
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .SingleOrDefaultAsync(value => value.Id == messageId && value.CommunityId == communityId && value.ChannelId == channelId);
        return message ?? throw new HubException("Message not found in this Community channel.");
    }

    private async Task<DirectConversation> RequireDirectConversationAsync(Guid conversationId, Guid accountId)
    {
        var conversation = await db.DirectConversations.SingleOrDefaultAsync(value => value.Id == conversationId &&
            (value.ParticipantAAccountId == accountId || value.ParticipantBAccountId == accountId));
        return conversation ?? throw new HubException("Direct conversation not found for this account.");
    }

    private async Task<DirectMessage> DirectMessageInContextAsync(Guid conversationId, Guid messageId)
    {
        var message = await db.DirectMessages
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .SingleOrDefaultAsync(value => value.Id == messageId && value.ConversationId == conversationId);
        return message ?? throw new HubException("Direct message not found in this conversation.");
    }

    private IClientProxy DirectParticipants(DirectConversation conversation) => Clients.Groups(
        AccountGroup(conversation.ParticipantAAccountId),
        AccountGroup(conversation.ParticipantBAccountId));

    private async Task BroadcastPresenceAsync(Guid accountId, PublicPresence publicPresence)
    {
        var relatedAccounts = await db.Friendships
            .Where(value => value.RequesterAccountId == accountId || value.AddresseeAccountId == accountId)
            .Select(value => value.RequesterAccountId == accountId ? value.AddresseeAccountId : value.RequesterAccountId)
            .Concat(db.DirectConversations
                .Where(value => value.ParticipantAAccountId == accountId || value.ParticipantBAccountId == accountId)
                .Select(value => value.ParticipantAAccountId == accountId ? value.ParticipantBAccountId : value.ParticipantAAccountId))
            .Distinct()
            .ToListAsync();
        relatedAccounts.Add(accountId);
        await Clients.Groups(relatedAccounts.Distinct().Select(AccountGroup).ToArray()).SendAsync(
            PresenceHubContract.PresenceChanged, new PresenceChangedEvent(accountId, publicPresence));
    }

    private async Task RequireChannelAsync(
        Guid communityId, Guid channelId, Guid accountId, CommunityPermission permission)
    {
        var access = await authorization.GetAccessAsync(communityId, accountId, db);
        if (!access.IsOwner && !await authorization.IsMemberAsync(communityId, accountId, db))
            throw new HubException("You are not a member of this Community.");
        if (!access.Has(permission))
            throw new HubException("You do not have permission to use this Community channel.");
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId))
            throw new HubException("Channel not found in this Community.");
    }

    private async Task<(IReadOnlyList<CommunityMentionDto> Mentions, HashSet<Guid> Recipients)> ValidateMentionsAsync(
        Guid communityId,
        Guid senderAccountId,
        string content,
        IReadOnlyList<CommunityMentionInput>? requested)
    {
        if (requested is null || requested.Count == 0) return ([], []);
        if (requested.Count > 16) throw new HubException("A message cannot contain more than 16 mention targets.");

        var access = await authorization.GetAccessAsync(communityId, senderAccountId, db);
        var memberIds = await db.CommunityMembers.Where(value => value.CommunityId == communityId)
            .Select(value => value.AccountId).ToListAsync();
        var roles = await db.CommunityRoles.Where(value => value.CommunityId == communityId).ToListAsync();
        var result = new List<CommunityMentionDto>();
        var recipients = new HashSet<Guid>();
        var unique = new HashSet<(CommunityMentionKind Kind, Guid? TargetId, int Start)>();

        foreach (var input in requested.OrderBy(value => value.Start))
        {
            if (input.Start < 0 || input.Length < 2 || input.Start + input.Length > content.Length)
                throw new HubException("A mention does not match the message content.");
            if (content[input.Start] != '@') throw new HubException("A mention must begin with @ in the message content.");
            if (!unique.Add((input.Kind, input.TargetId, input.Start))) continue;

            switch (input.Kind)
            {
                case CommunityMentionKind.Account when input.TargetId is { } accountId:
                {
                    if (!memberIds.Contains(accountId)) throw new HubException("Mentioned account is not a member of this Community.");
                    var account = await db.Accounts.AsNoTracking().SingleAsync(value => value.Id == accountId);
                    result.Add(new(input.Kind, accountId, input.Start, input.Length, $"@{account.DisplayName}"));
                    if (accountId != senderAccountId && await authorization.HasPermissionAsync(
                            communityId, accountId, CommunityPermission.ViewChannels, db)) recipients.Add(accountId);
                    break;
                }
                case CommunityMentionKind.Role when input.TargetId is { } roleId:
                {
                    var role = roles.SingleOrDefault(value => value.Id == roleId)
                        ?? throw new HubException("Mentioned role does not belong to this Community.");
                    if (!role.IsMentionable && !access.Has(CommunityPermission.MentionEveryone))
                        throw new HubException("You do not have permission to mention that role.");
                    result.Add(new(input.Kind, roleId, input.Start, input.Length, $"@{role.Name.TrimStart('@')}"));
                    var roleMembers = await db.CommunityMemberRoles
                        .Where(value => value.CommunityId == communityId && value.RoleId == roleId)
                        .Select(value => value.AccountId).ToListAsync();
                    foreach (var accountId in roleMembers.Where(value => value != senderAccountId))
                        if (await authorization.HasPermissionAsync(communityId, accountId, CommunityPermission.ViewChannels, db))
                            recipients.Add(accountId);
                    break;
                }
                case CommunityMentionKind.Everyone:
                    if (!access.Has(CommunityPermission.MentionEveryone))
                        throw new HubException("You do not have permission to mention everyone.");
                    result.Add(new(input.Kind, null, input.Start, input.Length, "@everyone"));
                    foreach (var accountId in memberIds.Where(value => value != senderAccountId))
                        if (await authorization.HasPermissionAsync(communityId, accountId, CommunityPermission.ViewChannels, db))
                            recipients.Add(accountId);
                    break;
                default:
                    throw new HubException("That mention target is invalid.");
            }
        }

        return (result, recipients);
    }

    private async Task<Guid> RequireAccountAsync() => (await RequireSessionAsync()).AccountId;
    private async Task<AccountSession> RequireSessionAsync() =>
        await GetSessionAsync() ?? throw new HubException("Your session is no longer valid. Sign in again.");

    private Task<AccountSession?> GetSessionAsync()
    {
        var http = Context.GetHttpContext();
        var token = http?.Request.Query["access_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            var authorizationHeader = http?.Request.Headers.Authorization.ToString();
            if (authorizationHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                token = authorizationHeader[7..].Trim();
        }
        return sessions.GetByTokenAsync(token ?? string.Empty, db);
    }

    private static string ValidContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new HubException("Messages cannot be empty.");
        var content = value.TrimEnd();
        if (content.Length > 4000) throw new HubException("Messages cannot exceed 4,000 characters.");
        return content;
    }

    private static string GroupName(Guid communityId, Guid channelId) => $"community:{communityId:N}:channel:{channelId:N}";
    private static string DirectGroup(Guid conversationId) => $"direct:{conversationId:N}";
    internal static string AccountGroup(Guid accountId) => $"account:{accountId:N}";
}
