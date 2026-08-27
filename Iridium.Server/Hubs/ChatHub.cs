using Iridium.Protocol;
using Iridium.Server.Api;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Iridium.Server.Configuration;
using Microsoft.Extensions.Options;
using Iridium.Server.Calls;
using Iridium.Server.Voice;

namespace Iridium.Server.Hubs;

public sealed class ChatHub(
    ConnectionCounter connections,
    PresenceTracker presence,
    IridiumDbContext db,
    SessionService sessions,
    CommunityAuthorizationService authorization,
    HistoricalAuthorPresentationService historicalAuthors,
    IOptions<NodeOptions> nodeOptions,
    ICommunityLimitsService limitService,
    ICallService calls,
    IMediaService media,
    INodeMediaSessionService nodeMedia,
    DirectCallAuthorizationService callAuthorization,
    VoiceConnectionRegistry voiceConnections,
    VoiceTraceLogger voiceTrace,
    CommunityVoiceRoomService communityVoice,
    ICommunityVoiceMediaGateway communityVoiceMedia,
    VoiceStreamRegistry voiceStreams,
    IHostEnvironment environment,
    ILogger<ChatHub> logger) : Hub
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
        voiceConnections.Connected(session.AccountId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, AccountGroup(session.AccountId));
        logger.LogDebug("SignalR connection {ConnectionId} registered for authenticated account {AccountId}.",
            Context.ConnectionId, session.AccountId);
        await BroadcastPresenceAsync(session.AccountId,
            presence.Connected(session.AccountId, session.Account.PreferredPresence));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.ContainsKey(CountedKey)) connections.Disconnected();
        if (Context.Items.TryGetValue(AccountKey, out var value) && value is Guid accountId)
        {
            voiceStreams.RemoveConnection(Context.ConnectionId, "ParticipantDisconnected");
            if (await communityVoice.LeaveAsync(Context.ConnectionId) is { } voiceLeave)
                await BroadcastVoiceAsync(voiceLeave.CommunityId, CommunityVoiceHubContract.ParticipantLeft,
                    new VoiceParticipantLeftEvent(voiceLeave.CommunityId, voiceLeave.ChannelId,
                        voiceLeave.Participant.AccountId, voiceLeave.Participant.ParticipantId, voiceLeave.Room));
            var connectionLoss = calls.DisconnectSignaling(Context.ConnectionId);
            if (connectionLoss is not null)
            {
                var ended = new CallStateEvent(connectionLoss.Call.Id, connectionLoss.Call.State,
                    "The selected signaling connection disconnected");
                voiceTrace.Log(connectionLoss.Call, accountId, Context.ConnectionId,
                    new VoiceDiagnosticReport(connectionLoss.Call.Id, "CallEnded", Reason: "SignalingConnectionDisconnected"));
                if (connectionLoss.RemainingConnectionId is { } remainingConnectionId)
                    await Clients.Client(remainingConnectionId).SendAsync(VoiceCallHubContract.Ended, ended);
                else
                    await Clients.Group(AccountGroup(connectionLoss.RemainingAccountId))
                        .SendAsync(VoiceCallHubContract.Cancelled, ended);
            }
            voiceConnections.Disconnected(accountId, Context.ConnectionId);
            logger.LogDebug("SignalR connection {ConnectionId} disconnected from authenticated account {AccountId}.",
                Context.ConnectionId, accountId);
            await BroadcastPresenceAsync(accountId, presence.Disconnected(accountId));
        }
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

    public async Task<IReadOnlyList<ActiveVoiceRoomDto>> GetCommunityVoiceRooms(Guid communityId)
    {
        var accountId = await RequireAccountAsync();
        var access = await authorization.GetAccessAsync(communityId, accountId, db);
        if (!access.Has(CommunityPermission.ViewChannels))
            throw new HubException("You do not have permission to view this Server's voice rooms.");
        var rooms = communityVoice.GetRooms(communityId);
        var visible = new List<ActiveVoiceRoomDto>();
        foreach (var room in rooms)
            if (await authorization.HasChannelPermissionAsync(communityId, room.ChannelId, accountId,
                    CommunityPermission.ViewChannels, db)) visible.Add(room);
        return visible;
    }

    public async Task<ActiveVoiceRoomDto> JoinVoiceChannel(Guid communityId, Guid channelId)
    {
        var session = await RequireSessionAsync();
        await RequireVoiceChannelAsync(communityId, channelId, session.AccountId, CommunityPermission.ConnectVoice);
        if (communityVoice.RoomFor(Context.ConnectionId) is { } currentRoom &&
            currentRoom != (communityId, channelId))
            voiceStreams.RemoveConnection(Context.ConnectionId, "SessionSwitched");
        var existingRoom = communityVoice.GetRooms(communityId).FirstOrDefault(value => value.ChannelId == channelId);
        if (communityVoiceMedia.MaximumParticipants is { } maximum &&
            existingRoom is not null && existingRoom.Participants.All(value => value.ParticipantId != Context.ConnectionId) &&
            existingRoom.Participants.Count >= maximum)
            throw new HubException($"Development Community voice rooms support at most {maximum} connected clients.");
        var names = await db.CommunityChannels.Where(value => value.CommunityId == communityId && value.Id == channelId)
            .Select(value => new { CommunityName = value.Community.Name, ChannelName = value.Name }).SingleAsync();
        var member = await db.CommunityMembers.Include(value => value.Account).Include(value => value.ProfilePreset)
            .ThenInclude(value => value!.AvatarPreset)
            .SingleAsync(value => value.CommunityId == communityId && value.AccountId == session.AccountId);
        var joined = await communityVoice.JoinAsync(communityId, channelId, session.AccountId, Context.ConnectionId,
            member.Account.DisplayName, session.Account.Username, presence.GetPublic(session.AccountId),
            names.CommunityName, names.ChannelName, null, member.Account.AvatarRevision);
        var voiceAccess = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!voiceAccess.Has(CommunityPermission.SpeakVoice) &&
            await communityVoice.SetStateAsync(Context.ConnectionId, true, false) is { } initialState)
        {
            var updatedRoom = communityVoice.GetRooms(communityId).Single(value => value.ChannelId == channelId);
            joined = joined with { Room = updatedRoom, Participant = initialState.Participant };
        }
        if (joined.PreviousRoom is { } previous)
            await BroadcastVoiceAsync(previous.CommunityId, CommunityVoiceHubContract.ParticipantLeft,
                new VoiceParticipantLeftEvent(previous.CommunityId, previous.ChannelId, previous.Participant.AccountId,
                    previous.Participant.ParticipantId, previous.Room));
        if (!joined.AlreadyJoined)
            await BroadcastVoiceAsync(communityId, CommunityVoiceHubContract.ParticipantJoined,
                new VoiceParticipantJoinedEvent(joined.Room, joined.Participant));
        return joined.Room;
    }

    public async Task LeaveVoiceChannel()
    {
        await RequireAccountAsync();
        var endedStreams = voiceStreams.RemoveConnection(Context.ConnectionId, "VoiceSessionEnded");
        if (await communityVoice.LeaveAsync(Context.ConnectionId) is not { } left) return;
        foreach (var ended in endedStreams)
            await Clients.Clients(left.Room?.Participants.Select(value => value.ParticipantId) ?? [])
                .SendAsync(VoiceStreamHubContract.Ended, ended);
        await BroadcastVoiceAsync(left.CommunityId, CommunityVoiceHubContract.ParticipantLeft,
            new VoiceParticipantLeftEvent(left.CommunityId, left.ChannelId, left.Participant.AccountId,
                left.Participant.ParticipantId, left.Room));
    }

    public async Task<CommunityVoiceMediaSessionDto> GetCommunityVoiceMediaSession()
    {
        var accountId = await RequireAccountAsync();
        var room = communityVoice.RoomFor(Context.ConnectionId)
            ?? throw new HubException("Join a Community voice channel first.");
        var access = await authorization.GetChannelAccessAsync(room.CommunityId, room.ChannelId, accountId, db);
        return await communityVoiceMedia.PrepareSessionAsync(room.CommunityId, room.ChannelId,
            Context.ConnectionId, accountId, access.Has(CommunityPermission.ShareScreen), Context.ConnectionAborted);
    }

    public async Task SendCommunityVoiceMediaOffer(string targetParticipantId, Guid negotiationId,
        WebRtcSessionDescription description)
    {
        RequireCommunityMediaRoute(targetParticipantId);
        if (!string.Equals(description.Type, "offer", StringComparison.OrdinalIgnoreCase))
            throw new HubException("A Community media offer must have type offer.");
        CommunityVoiceDiagnostic("OfferCreated", targetParticipantId, negotiationId);
        await Clients.Client(targetParticipantId).SendAsync(CommunityVoiceHubContract.MediaOffer,
            new CommunityVoiceMediaDescriptionEvent(Context.ConnectionId, negotiationId, description));
    }

    public async Task SendCommunityVoiceMediaAnswer(string targetParticipantId, Guid negotiationId,
        WebRtcSessionDescription description)
    {
        RequireCommunityMediaRoute(targetParticipantId);
        if (!string.Equals(description.Type, "answer", StringComparison.OrdinalIgnoreCase))
            throw new HubException("A Community media answer must have type answer.");
        CommunityVoiceDiagnostic("AnswerCreated", targetParticipantId, negotiationId);
        await Clients.Client(targetParticipantId).SendAsync(CommunityVoiceHubContract.MediaAnswer,
            new CommunityVoiceMediaDescriptionEvent(Context.ConnectionId, negotiationId, description));
    }

    public async Task SendCommunityVoiceMediaIceCandidate(string targetParticipantId, Guid negotiationId,
        WebRtcIceCandidate candidate)
    {
        RequireCommunityMediaRoute(targetParticipantId);
        CommunityVoiceDiagnostic("IceGenerated", targetParticipantId, negotiationId);
        await Clients.Client(targetParticipantId).SendAsync(CommunityVoiceHubContract.MediaIceCandidate,
            new CommunityVoiceMediaIceCandidateEvent(Context.ConnectionId, negotiationId, candidate));
    }

    public async Task<IReadOnlyList<PublishedVoiceStreamDto>> GetPublishedVoiceStreams(
        VoiceMediaSessionKind sessionKind, Guid sessionId)
    {
        await RequireVoiceStreamSessionAsync(sessionKind, sessionId, publishing: false);
        return voiceStreams.Get(sessionKind, sessionId);
    }

    public async Task<PublishedVoiceStreamDto> PublishVoiceStream(VoiceMediaSessionKind sessionKind,
        Guid sessionId, PublishVoiceStreamRequest request)
    {
        var authorizationResult = await RequireVoiceStreamSessionAsync(sessionKind, sessionId, publishing: true);
        VoiceStreamPublishResult result;
        try
        {
            result = voiceStreams.Publish(sessionKind, sessionId, authorizationResult.AccountId,
                authorizationResult.DisplayName, Context.ConnectionId, request);
        }
        catch (InvalidOperationException exception) { throw new HubException(exception.Message); }
        if (result.Replaced is not null)
            await Clients.Clients(authorizationResult.OtherParticipantIds)
                .SendAsync(VoiceStreamHubContract.Ended, result.Replaced);
        await Clients.Clients(authorizationResult.OtherParticipantIds)
            .SendAsync(VoiceStreamHubContract.Published, new VoiceStreamPublishedEvent(result.Stream));
        return result.Stream;
    }

    public async Task StopPublishedVoiceStream(VoiceMediaSessionKind sessionKind, Guid sessionId,
        Guid streamId, string reason = "UserStoppedInIridium")
    {
        var authorizationResult = await RequireVoiceStreamSessionAsync(sessionKind, sessionId, publishing: false);
        var ended = voiceStreams.Stop(sessionKind, sessionId, streamId, Context.ConnectionId,
            string.IsNullOrWhiteSpace(reason) ? "UserStoppedInIridium" : reason);
        if (ended is null) return;
        await Clients.Clients(authorizationResult.OtherParticipantIds).SendAsync(VoiceStreamHubContract.Ended, ended);
    }

    public async Task<PublishedVoiceStreamDto> UpdatePublishedVoiceStream(VoiceMediaSessionKind sessionKind,
        Guid sessionId, Guid streamId, bool hasAudio)
    {
        var authorizationResult = await RequireVoiceStreamSessionAsync(sessionKind, sessionId, publishing: true);
        var stream = voiceStreams.Update(sessionKind, sessionId, streamId, Context.ConnectionId, hasAudio)
            ?? throw new HubException("That stream is no longer available.");
        await Clients.Clients(authorizationResult.OtherParticipantIds)
            .SendAsync(VoiceStreamHubContract.Published, new VoiceStreamPublishedEvent(stream));
        return stream;
    }

    public async Task WatchVoiceStream(VoiceMediaSessionKind sessionKind, Guid sessionId, Guid streamId)
    {
        await RequireVoiceStreamSessionAsync(sessionKind, sessionId, publishing: false);
        if (!voiceStreams.Watch(Context.ConnectionId, sessionKind, sessionId, streamId))
            throw new HubException("That stream is no longer available.");
    }

    public async Task StopWatchingVoiceStream(Guid streamId)
    {
        await RequireAccountAsync();
        voiceStreams.StopWatching(Context.ConnectionId, streamId);
    }

    public async Task SetVoiceParticipantState(bool muted, bool deafened)
    {
        var accountId = await RequireAccountAsync();
        var room = communityVoice.RoomFor(Context.ConnectionId)
            ?? throw new HubException("Join a Community voice channel first.");
        if (!muted && !deafened)
            await RequireVoiceChannelAsync(room.CommunityId, room.ChannelId, accountId,
                CommunityPermission.SpeakVoice);
        var changed = await communityVoice.SetStateAsync(Context.ConnectionId, muted, deafened)
            ?? throw new HubException("Your voice membership is no longer active.");
        await BroadcastVoiceAsync(changed.CommunityId, CommunityVoiceHubContract.ParticipantStateChanged, changed);
    }

    public async Task SetVoiceParticipantSpeaking(bool speaking)
    {
        var accountId = await RequireAccountAsync();
        var room = communityVoice.RoomFor(Context.ConnectionId)
            ?? throw new HubException("Join a Community voice channel first.");
        if (speaking)
            await RequireVoiceChannelAsync(room.CommunityId, room.ChannelId, accountId,
                CommunityPermission.SpeakVoice);
        var changed = communityVoice.SetSpeaking(Context.ConnectionId, speaking);
        if (changed is null) return;
        await BroadcastVoiceAsync(changed.CommunityId, CommunityVoiceHubContract.ParticipantStateChanged, changed);
    }

    public async Task<ChannelMessageDto> SendMessage(
        Guid communityId,
        Guid channelId,
        SendChannelMessageRequest request)
    {
        var session = await RequireSessionAsync();
        await RequireChannelAsync(communityId, channelId, session.AccountId, CommunityPermission.SendMessages);
        var forumPost = await db.CommunityForumPosts.Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage)
            .SingleOrDefaultAsync(value => value.CommunityId == communityId && value.DiscussionChannelId == channelId);
        if (forumPost?.IsLocked == true && !await authorization.HasChannelPermissionAsync(
                communityId, channelId, session.AccountId, CommunityPermission.ManageMessages, db))
            throw new HubException("This Forum post is locked.");
        if (request.ClientMessageId == Guid.Empty) throw new HubException("The client message identifier is invalid.");
        if (request.ClientMessageId is { } existingClientId)
        {
            var existing = await db.ChannelMessages.Include(value => value.AuthorAccount)
                .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
                .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
                .Include(value => value.Attachments)
                .IncludeForwardedSnapshot()
                .SingleOrDefaultAsync(value => value.AuthorAccountId == session.AccountId &&
                    value.CommunityId == communityId && value.ChannelId == channelId &&
                    value.ClientMessageId == existingClientId);
            if (existing is not null) return await ChannelMessageMapper.ResolveCommunityProfileAsync(
                ChannelMessageMapper.ToDto(existing), db);
        }
        var attachments = await ValidateAttachmentsAsync(request.AttachmentIds, session.AccountId);
        if (attachments.Count > 0)
            await RequireChannelAsync(communityId, channelId, session.AccountId, CommunityPermission.AttachFiles);
        var content = ValidContent(request.Content, attachments.Count > 0, communityId);
        var (mentions, recipients) = await ValidateMentionsAsync(
            communityId, channelId, session.AccountId, content, request.Mentions);

        ChannelMessage? reply = null;
        if (request.ReplyToMessageId is { } replyId)
        {
            reply = await db.ChannelMessages.Include(value => value.AuthorAccount)
                .Include(value => value.Attachments)
                .SingleOrDefaultAsync(value => value.Id == replyId && value.CommunityId == communityId && value.ChannelId == channelId);
            if (reply is null || reply.IsDeleted) throw new HubException("The message being replied to is no longer available.");
        }

        var message = new ChannelMessage
        {
            Id = Guid.NewGuid(),
            CommunityId = communityId,
            ChannelId = channelId,
            AuthorAccountId = session.AccountId,
            ClientMessageId = request.ClientMessageId,
            AuthorAccount = session.Account,
            Channel = null!,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            ReplyToMessageId = reply?.Id,
            ReplyToMessage = reply,
            MentionsJson = mentions.Count == 0 ? null : JsonSerializer.Serialize(mentions)
        };
        await historicalAuthors.CaptureAsync(message, communityId, session.AccountId);
        foreach (var attachment in attachments)
        {
            attachment.ChannelMessageId = message.Id;
            attachment.ChannelMessage = message;
            message.Attachments.Add(attachment);
        }
        db.ChannelMessages.Add(message);
        foreach (var recipientId in recipients)
        {
            db.CommunityMentionNotifications.Add(new CommunityMentionNotification
            {
                MessageId = message.Id,
                AccountId = recipientId,
                CommunityId = communityId,
                ChannelId = channelId,
                CreatedAt = message.CreatedAt,
                Message = message,
                Account = null!
            });
        }
        if (forumPost is not null)
        {
            forumPost.ReplyCount++;
            forumPost.LastActivityAt = message.CreatedAt;
            forumPost.UpdatedAt = message.CreatedAt;
        }
        await db.SaveChangesAsync();
        var result = await ChannelMessageMapper.ResolveCommunityProfileAsync(ChannelMessageMapper.ToDto(message), db);
        await Clients.Group(GroupName(communityId, channelId)).SendAsync(ChatHubContract.MessageCreated, result);
        var communityRecipients = await db.CommunityMembers
            .Where(value => value.CommunityId == communityId && value.AccountId != session.AccountId)
            .Select(value => value.AccountId).ToListAsync();
        foreach (var recipient in communityRecipients.ToArray())
            if (!await authorization.HasChannelPermissionAsync(communityId, channelId, recipient,
                    CommunityPermission.ViewChannels, db)) communityRecipients.Remove(recipient);
        if (communityRecipients.Count > 0)
            await Clients.Groups(communityRecipients.Select(AccountGroup).ToArray()).SendAsync(
                CommunityHubContract.ChannelActivity,
                new CommunityChannelActivityEvent(communityId, forumPost?.ForumChannelId ?? channelId, session.AccountId));
        if (recipients.Count > 0)
        {
            var mentionEvent = new CommunityMentionReceivedEvent(communityId, channelId, message.Id, session.AccountId);
            await Clients.Groups(recipients.Select(AccountGroup).ToArray())
                .SendAsync(CommunityMentionHubContract.Received, mentionEvent);
        }
        if (forumPost is not null) await PublishForumPostAsync(forumPost, "activity", session.AccountId);
        return result;
    }

    public async Task<ForwardMessagesResultDto> ForwardMessage(ForwardMessageRequest request)
    {
        var session = await RequireSessionAsync();
        if (request.Destinations is null || request.Destinations.Count == 0)
            throw new HubException("Select at least one forwarding destination.");
        if (request.Destinations.Count > MessageForwardingLimits.MaximumDestinations)
            throw new HubException($"Messages can be forwarded to at most {MessageForwardingLimits.MaximumDestinations} destinations.");
        if (request.Destinations.Distinct().Count() != request.Destinations.Count)
            throw new HubException("A forwarding destination was selected more than once.");

        var source = await LoadForwardSourceAsync(request.Source, session.AccountId);
        var snapshot = source.ExistingSnapshot ?? new ForwardedMessageSnapshot
        {
            Id = Guid.NewGuid(),
            Content = source.Content,
            MentionsJson = source.MentionsJson,
            SourceCommunityId = source.SourceCommunityId,
            SourceChannelId = source.SourceChannelId,
            SourceMessageId = source.SourceMessageId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (source.ExistingSnapshot is null)
            foreach (var attachment in source.Attachments)
                snapshot.Attachments.Add(new ForwardedMessageAttachment
                {
                    ForwardedMessageSnapshotId = snapshot.Id,
                    AttachmentId = attachment.Id,
                    Snapshot = snapshot,
                    Attachment = attachment
                });

        var note = string.IsNullOrWhiteSpace(request.Note) ? string.Empty : request.Note.TrimEnd();
        var channelDestinations = new List<(ForwardDestinationSelectionDto Selection, CommunityChannel Channel)>();
        var directDestinations = new List<(ForwardDestinationSelectionDto Selection, DirectConversation Conversation)>();
        foreach (var destination in request.Destinations)
        {
            if (destination.Kind == MessageLocationKind.CommunityChannel)
            {
                if (destination.CommunityId is not { } communityId || destination.ChannelId is not { } channelId)
                    throw new HubException("That Server channel destination is invalid.");
                var channel = await db.CommunityChannels.SingleOrDefaultAsync(value =>
                    value.CommunityId == communityId && value.Id == channelId && value.Kind == CommunityChannelKind.Text);
                if (channel is null) throw new HubException("That text channel is unavailable.");
                await RequireChannelAsync(communityId, channelId, session.AccountId,
                    CommunityPermission.ViewChannels | CommunityPermission.SendMessages);
                if (snapshot.Attachments.Count > 0)
                    await RequireChannelAsync(communityId, channelId, session.AccountId, CommunityPermission.AttachFiles);
                _ = ValidContent(note, allowEmpty: true, communityId);
                channelDestinations.Add((destination, channel));
            }
            else if (destination.Kind == MessageLocationKind.DirectConversation)
            {
                if (destination.ConversationId is not { } conversationId)
                    throw new HubException("That Direct Message destination is invalid.");
                var conversation = await RequireDirectConversationAsync(conversationId, session.AccountId);
                _ = ValidContent(note, allowEmpty: true);
                directDestinations.Add((destination, conversation));
            }
            else throw new HubException("That forwarding destination is not supported.");
        }

        if (source.ExistingSnapshot is null) db.ForwardedMessageSnapshots.Add(snapshot);
        var now = DateTimeOffset.UtcNow;
        var channelMessages = new List<ChannelMessage>();
        foreach (var (_, channel) in channelDestinations)
        {
            var message = new ChannelMessage
            {
                Id = Guid.NewGuid(), CommunityId = channel.CommunityId, ChannelId = channel.Id,
                AuthorAccountId = session.AccountId, AuthorAccount = session.Account, Channel = channel,
                Content = note, CreatedAt = now, ForwardedMessageSnapshotId = snapshot.Id,
                ForwardedMessageSnapshot = snapshot
            };
            await historicalAuthors.CaptureAsync(message, channel.CommunityId, session.AccountId);
            channelMessages.Add(message);
            db.ChannelMessages.Add(message);
        }
        var directMessages = directDestinations.Select(value => new DirectMessage
        {
            Id = Guid.NewGuid(), ConversationId = value.Conversation.Id, Conversation = value.Conversation,
            AuthorAccountId = session.AccountId, AuthorAccount = session.Account, Content = note, CreatedAt = now,
            ForwardedMessageSnapshotId = snapshot.Id, ForwardedMessageSnapshot = snapshot
        }).ToList();
        db.DirectMessages.AddRange(directMessages);
        await db.SaveChangesAsync();

        var channelResults = new List<ChannelMessageDto>();
        foreach (var message in channelMessages)
        {
            var result = await ChannelMessageMapper.ResolveCommunityProfileAsync(ChannelMessageMapper.ToDto(message), db);
            channelResults.Add(result);
            await Clients.Group(GroupName(message.CommunityId, message.ChannelId))
                .SendAsync(ChatHubContract.MessageCreated, result);
            var recipients = await db.CommunityMembers.AsNoTracking()
                .Where(value => value.CommunityId == message.CommunityId && value.AccountId != session.AccountId)
                .Select(value => value.AccountId).ToListAsync();
            foreach (var recipient in recipients.ToArray())
                if (!await authorization.HasChannelPermissionAsync(message.CommunityId, message.ChannelId, recipient,
                        CommunityPermission.ViewChannels, db)) recipients.Remove(recipient);
            if (recipients.Count > 0)
                await Clients.Groups(recipients.Select(AccountGroup).ToArray()).SendAsync(
                    CommunityHubContract.ChannelActivity,
                    new CommunityChannelActivityEvent(message.CommunityId, message.ChannelId, session.AccountId));
        }
        var directResults = new List<DirectMessageDto>();
        foreach (var message in directMessages)
        {
            var result = DirectMessageMapper.ToDto(message);
            directResults.Add(result);
            await DirectParticipants(message.Conversation).SendAsync(DirectMessageHubContract.MessageCreated, result);
        }
        return new(channelResults, directResults);
    }

    private async Task<ForwardSource> LoadForwardSourceAsync(ForwardMessageSourceDto source, Guid accountId)
    {
        if (source.Kind == MessageLocationKind.CommunityChannel)
        {
            if (source.CommunityId is not { } communityId || source.ChannelId is not { } channelId)
                throw new HubException("The source message location is invalid.");
            await RequireChannelAsync(communityId, channelId, accountId,
                CommunityPermission.ViewChannels | CommunityPermission.ReadMessageHistory);
            var message = await db.ChannelMessages.Include(value => value.Attachments).IncludeForwardedSnapshot()
                .SingleOrDefaultAsync(value => value.Id == source.MessageId && value.CommunityId == communityId &&
                    value.ChannelId == channelId && !value.IsDeleted);
            if (message is null) throw new HubException("The source message is no longer available.");
            return message.ForwardedMessageSnapshot is { } forwarded
                ? ForwardSource.FromExisting(forwarded)
                : new(message.Content, message.MentionsJson, message.Attachments.ToArray(), null,
                    communityId, channelId, message.Id);
        }

        if (source.Kind == MessageLocationKind.DirectConversation && source.ConversationId is { } conversationId)
        {
            _ = await RequireDirectConversationAsync(conversationId, accountId);
            var message = await db.DirectMessages.Include(value => value.Attachments).IncludeForwardedSnapshot()
                .SingleOrDefaultAsync(value => value.Id == source.MessageId && value.ConversationId == conversationId &&
                    !value.IsDeleted && value.Kind == MessageKind.User);
            if (message is null) throw new HubException("The source message is no longer available.");
            return message.ForwardedMessageSnapshot is { } forwarded
                ? ForwardSource.FromExisting(forwarded)
                : new(message.Content, null, message.Attachments.ToArray(), null, null, null, null);
        }
        throw new HubException("The source message location is invalid.");
    }

    private sealed record ForwardSource(string Content, string? MentionsJson, IReadOnlyList<Attachment> Attachments,
        ForwardedMessageSnapshot? ExistingSnapshot, Guid? SourceCommunityId, Guid? SourceChannelId,
        Guid? SourceMessageId)
    {
        public static ForwardSource FromExisting(ForwardedMessageSnapshot snapshot) =>
            new(snapshot.Content, snapshot.MentionsJson, [], snapshot, snapshot.SourceCommunityId,
                snapshot.SourceChannelId, snapshot.SourceMessageId);
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
        var rootForumPost = await db.CommunityForumPosts.Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage)
            .SingleOrDefaultAsync(value => value.RootMessageId == messageId);
        if (message.AuthorAccountId != accountId) throw new HubException("You can only edit your own messages.");
        if (message.IsDeleted) throw new HubException("Deleted messages cannot be edited.");

        message.Content = ValidContent(request.Content, allowEmpty: message.ForwardedMessageSnapshotId is not null,
            communityId: communityId);
        message.MentionsJson = null;
        message.EditedAt = DateTimeOffset.UtcNow;
        if (rootForumPost is not null) rootForumPost.UpdatedAt = message.EditedAt.Value;
        await db.SaveChangesAsync();
        var result = await ChannelMessageMapper.ResolveCommunityProfileAsync(ChannelMessageMapper.ToDto(message), db);
        await Clients.Group(GroupName(communityId, channelId)).SendAsync(ChatHubContract.MessageUpdated, result);
        if (rootForumPost is not null) await PublishForumPostAsync(rootForumPost, "updated", accountId);
        return result;
    }

    public async Task DeleteMessage(Guid communityId, Guid channelId, Guid messageId)
    {
        var accountId = await RequireAccountAsync();
        await RequireChannelAsync(communityId, channelId, accountId, CommunityPermission.ViewChannels);
        var message = await MessageInContextAsync(communityId, channelId, messageId);
        if (await db.CommunityForumPosts.AnyAsync(value => value.RootMessageId == messageId))
            throw new HubException("Delete the Forum post rather than deleting its root message.");
        var mayModerate = await authorization.HasChannelPermissionAsync(
            communityId, channelId, accountId, CommunityPermission.ManageMessages, db);
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
        var forumPost = await db.CommunityForumPosts.Include(value => value.AuthorAccount)
            .Include(value => value.RootMessage)
            .SingleOrDefaultAsync(value => value.CommunityId == communityId && value.DiscussionChannelId == channelId);
        if (forumPost is not null)
        {
            forumPost.ReplyCount = await db.ChannelMessages.CountAsync(value => value.ChannelId == channelId &&
                value.Id != forumPost.RootMessageId && !value.IsDeleted);
            forumPost.LastActivityAt = await db.ChannelMessages.Where(value => value.ChannelId == channelId && !value.IsDeleted)
                .MaxAsync(value => (DateTimeOffset?)value.CreatedAt) ?? forumPost.CreatedAt;
            forumPost.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await PublishForumPostAsync(forumPost, "updated", accountId);
        }
    }

    public async Task JoinDirectConversation(Guid conversationId)
    {
        var accountId = await RequireAccountAsync();
        await RequireDirectConversationAsync(conversationId, accountId);
        await Groups.AddToGroupAsync(Context.ConnectionId, DirectGroup(conversationId));
    }

    public Task LeaveDirectConversation(Guid conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DirectGroup(conversationId));

    public async Task<CallSessionDto> StartDirectVoiceCall(Guid conversationId)
    {
        var session = await RequireSessionAsync();
        var parties = await callAuthorization.AuthorizeStartAsync(conversationId, session.AccountId);
        var call = calls.CreateDirect(parties.ConversationId, parties.CallerId, parties.CallerDisplayName,
            parties.CalleeId, parties.CalleeDisplayName, Context.ConnectionId);
        var conversation = await RequireDirectConversationAsync(parties.ConversationId, session.AccountId);
        var callStartedMessage = new DirectMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Conversation = conversation,
            AuthorAccountId = session.AccountId,
            AuthorAccount = session.Account,
            Kind = MessageKind.CallStarted,
            RelatedCallId = call.Id,
            Content = string.Empty,
            CreatedAt = call.CreatedAt
        };
        db.DirectMessages.Add(callStartedMessage);
        try { await db.SaveChangesAsync(); }
        catch
        {
            calls.Cancel(call.Id, session.AccountId);
            throw;
        }
        var callStartedEvent = DirectMessageMapper.ToDto(callStartedMessage);
        await DirectParticipants(conversation).SendAsync(DirectMessageHubContract.MessageCreated, callStartedEvent);
        VoiceDiagnostic("CallCreated", call.Id, session.AccountId, parties.CalleeId, null, null, null);
        await Clients.Group(AccountGroup(parties.CalleeId)).SendAsync(VoiceCallHubContract.Incoming,
            new IncomingCallEvent(call.Id, parties.ConversationId, parties.CallerId, parties.CallerDisplayName,
                call.CreatedAt, call.ExpiresAt));
        VoiceDiagnostic("IncomingCallSent", call.Id, session.AccountId, parties.CalleeId, null, null, null);
        return call;
    }

    public async Task AcceptVoiceCall(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        VoiceDiagnostic("AcceptRequested", callId, accountId, null, null, null, null);
        var call = calls.Accept(callId, accountId, Context.ConnectionId);
        var callerRoute = calls.RequireSignalingRoute(callId, accountId, Context.ConnectionId, CallState.Active);
        var signalId = Guid.NewGuid();
        VoiceDiagnostic("CallAccepted", callId, accountId, call.CallerAccountId, null, null,
            signalId, callerRoute.TargetConnectionId);
        await Clients.Client(callerRoute.TargetConnectionId).SendAsync(VoiceCallHubContract.Accepted,
            new CallStateEvent(callId, CallState.Active, SignalId: signalId));
        await Clients.OthersInGroup(AccountGroup(accountId)).SendAsync(VoiceCallHubContract.Cancelled,
            new CallStateEvent(callId, CallState.Cancelled, "Answered in another tab"));
    }

    public async Task RejectVoiceCall(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        VoiceDiagnostic("DeclineRequested", callId, accountId, null, null, null, null);
        var call = calls.Reject(callId, accountId);
        VoiceDiagnostic("CallRejected", callId, accountId, call.CallerAccountId, null, null, null);
        await OtherCallParticipant(call, accountId).SendAsync(VoiceCallHubContract.Rejected,
            new CallStateEvent(callId, CallState.Rejected));
        await Clients.OthersInGroup(AccountGroup(accountId)).SendAsync(VoiceCallHubContract.Cancelled,
            new CallStateEvent(callId, CallState.Cancelled, "Declined in another tab"));
    }

    public async Task CancelVoiceCall(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        VoiceDiagnostic("CancelRequested", callId, accountId, null, null, null, null);
        calls.RequireSelectedConnection(callId, accountId, Context.ConnectionId, CallState.Ringing);
        var call = calls.Cancel(callId, accountId);
        VoiceDiagnostic("CallCancelled", callId, accountId,
            call.Participants.Single(value => value.AccountId != accountId).AccountId, null, null, null);
        await OtherCallParticipant(call, accountId).SendAsync(VoiceCallHubContract.Cancelled,
            new CallStateEvent(callId, CallState.Cancelled));
        await Clients.OthersInGroup(AccountGroup(accountId)).SendAsync(VoiceCallHubContract.Cancelled,
            new CallStateEvent(callId, CallState.Cancelled));
    }

    public async Task HangUpVoiceCall(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        VoiceDiagnostic("HangupRequested", callId, accountId, null, null, null, null);
        var route = calls.RequireSignalingRoute(callId, accountId, Context.ConnectionId, CallState.Active);
        var call = calls.HangUp(callId, accountId);
        voiceStreams.RemoveSession(VoiceMediaSessionKind.DirectCall, callId, "VoiceSessionEnded");
        VoiceDiagnostic("CallEnded", callId, accountId, route.TargetAccountId, null, null, null,
            route.TargetConnectionId);
        await Clients.Client(route.TargetConnectionId).SendAsync(VoiceCallHubContract.Ended,
            new CallStateEvent(callId, call.State));
        await Clients.OthersInGroup(AccountGroup(accountId)).SendAsync(VoiceCallHubContract.Ended,
            new CallStateEvent(callId, call.State));
    }

    public async Task SetCallParticipantState(Guid callId, bool muted, bool deafened, CallConnectionState connectionState)
    {
        var accountId = await RequireAccountAsync();
        calls.RequireSignalingRoute(callId, accountId, Context.ConnectionId, CallState.Active);
        var previousParticipant = calls.RequireParticipant(callId, accountId, CallState.Active).Participants
            .Single(value => value.AccountId == accountId);
        if (previousParticipant.IsMuted == muted && previousParticipant.IsDeafened == deafened &&
            previousParticipant.ConnectionState == connectionState)
        {
            calls.TouchSignaling(callId, accountId, Context.ConnectionId);
            return;
        }
        var update = calls.SetParticipantState(callId, accountId, muted, deafened, connectionState);
        var route = calls.RequireSignalingRoute(callId, accountId, Context.ConnectionId, CallState.Active);
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        var stateCall = calls.RequireParticipant(callId, accountId, CallState.Active);
        voiceTrace.Log(stateCall, accountId, Context.ConnectionId, new VoiceDiagnosticReport(callId,
            "PeerStateChanged", OldState: previousParticipant.ConnectionState.ToString(),
            NewState: connectionState.ToString(), ConnectionState: connectionState.ToString()));
        await Clients.Client(route.TargetConnectionId).SendAsync(VoiceCallHubContract.ParticipantStateChanged, update);
    }

    public async Task SetCallParticipantSpeaking(Guid callId, bool isSpeaking)
    {
        var accountId = await RequireAccountAsync();
        var route = calls.RequireSignalingRoute(callId, accountId, Context.ConnectionId, CallState.Active);
        var update = calls.SetParticipantSpeaking(callId, accountId, isSpeaking);
        await Clients.Client(route.TargetConnectionId)
            .SendAsync(VoiceCallHubContract.ParticipantSpeakingChanged, update);
    }

    public async Task HeartbeatVoiceCall(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        calls.TouchSignaling(callId, accountId, Context.ConnectionId);
    }

    public async Task RequestCallMediaRetry(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        var call = calls.RequireParticipant(callId, accountId, CallState.Active);
        var route = calls.RequireSignalingRoute(callId, accountId, Context.ConnectionId, CallState.Active);
        VoiceDiagnostic("RetryRequested", callId, accountId, route.TargetAccountId, null, null, null,
            route.TargetConnectionId);
        logger.LogDebug("Media retry requested for call {CallId} by participant {AccountId}.", callId, accountId);
        await Clients.Client(route.TargetConnectionId).SendAsync(
            VoiceCallHubContract.MediaRetryRequested, new CallStateEvent(call.Id, call.State));
    }

    public async Task<CallMediaConfigurationDto> GetCallMediaConfiguration(Guid callId)
    {
        var accountId = await RequireAccountAsync();
        var developmentConfiguration = media.GetConfiguration();
        if (developmentConfiguration.Mode == MediaMode.DirectWebRtc)
        {
            calls.RequireParticipant(callId, accountId, CallState.Ringing, CallState.Active);
            return developmentConfiguration;
        }
        calls.RequireParticipant(callId, accountId, CallState.Active);
        calls.RequireSelectedConnection(callId, accountId, Context.ConnectionId, CallState.Active);
        if (!nodeMedia.Enabled) throw new HubException("Voice media is not configured on this Node.");
        return new CallMediaConfigurationDto(MediaMode.NodeSfu, [],
            nodeMedia.CreateDirectCallSession(callId, accountId));
    }

    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    public async Task ReportVoiceDiagnostic(VoiceDiagnosticReport report)
    {
        var accountId = await RequireAccountAsync();
        if (!voiceTrace.Enabled) return;
        if (report.CallId == Guid.Empty || string.IsNullOrWhiteSpace(report.Event) || report.Event.Length > 64 ||
            report.PeerGeneration is < 0 or > 1000 || report.NegotiationGeneration is < 0 or > 1000)
            throw new HubException("The voice diagnostic report is invalid.");
        var call = calls.RequireParticipant(report.CallId, accountId,
            CallState.Ringing, CallState.Active, CallState.Ended, CallState.Rejected, CallState.Cancelled);
        voiceTrace.Log(call, accountId, Context.ConnectionId, report);
    }

    public async Task<CallSessionDto?> GetCurrentCall()
    {
        var accountId = await RequireAccountAsync();
        return calls.CurrentFor(accountId, Context.ConnectionId);
    }

    public async Task SendWebRtcOffer(Guid callId, Guid negotiationId, int negotiationGeneration,
        int peerGeneration, Guid signalId, WebRtcNegotiationKind negotiationKind,
        WebRtcSessionDescription description)
    {
        var sender = await RequireAccountAsync();
        ValidateDiagnosticSignal(signalId, negotiationGeneration, peerGeneration);
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        VoiceDiagnostic("OfferReceivedByServer", callId, sender, null, negotiationGeneration, peerGeneration, signalId);
        var route = media.AuthorizeOffer(callId, sender, Context.ConnectionId, negotiationId, negotiationKind, description);
        var authorizedCall = calls.RequireParticipant(callId, sender, CallState.Active);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: authorized {NegotiationKind} offer from {SenderRole} participant {SenderAccountId}.",
            callId, negotiationId, negotiationKind,
            authorizedCall.CallerAccountId == sender ? "caller" : "callee", sender);
        if (!route.ShouldForward)
        {
            VoiceDiagnostic("OfferIgnoredByServer", callId, sender, route.TargetAccountId,
                negotiationGeneration, peerGeneration, signalId, route.TargetConnectionId);
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: WebRTC offer ignored by server ({IgnoreReason}).",
                callId, negotiationId, route.IgnoreReason);
            return;
        }
        VoiceDiagnostic("OfferForwarded", callId, sender, route.TargetAccountId,
            negotiationGeneration, peerGeneration, signalId, route.TargetConnectionId);
        await Clients.Client(route.TargetConnectionId).SendAsync(VoiceCallHubContract.Offer,
            new WebRtcDescriptionEvent(callId, sender, negotiationId, negotiationGeneration, peerGeneration, signalId,
                description, route.NegotiationKind));
    }

    public async Task SendWebRtcAnswer(Guid callId, Guid negotiationId, int negotiationGeneration,
        int peerGeneration, Guid signalId, WebRtcSessionDescription description)
    {
        var sender = await RequireAccountAsync();
        ValidateDiagnosticSignal(signalId, negotiationGeneration, peerGeneration);
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        VoiceDiagnostic("AnswerReceivedByServer", callId, sender, null, negotiationGeneration, peerGeneration, signalId);
        var route = media.AuthorizeAnswer(callId, sender, Context.ConnectionId, negotiationId, description);
        if (!route.ShouldForward)
        {
            VoiceDiagnostic("AnswerIgnoredByServer", callId, sender, route.TargetAccountId,
                negotiationGeneration, peerGeneration, signalId, route.TargetConnectionId);
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: WebRTC answer ignored by server ({IgnoreReason}).",
                callId, negotiationId, route.IgnoreReason);
            return;
        }
        VoiceDiagnostic("AnswerForwarded", callId, sender, route.TargetAccountId,
            negotiationGeneration, peerGeneration, signalId, route.TargetConnectionId);
        await Clients.Client(route.TargetConnectionId).SendAsync(VoiceCallHubContract.Answer,
            new WebRtcDescriptionEvent(callId, sender, negotiationId, negotiationGeneration, peerGeneration, signalId,
                description, route.NegotiationKind));
    }

    public async Task SendWebRtcIceCandidate(Guid callId, Guid negotiationId, int negotiationGeneration,
        int peerGeneration, Guid signalId, WebRtcIceCandidate candidate)
    {
        var sender = await RequireAccountAsync();
        ValidateDiagnosticSignal(signalId, negotiationGeneration, peerGeneration);
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        VoiceDiagnostic("IceReceivedByServer", callId, sender, null, negotiationGeneration, peerGeneration, signalId);
        var route = media.AuthorizeIceCandidate(callId, sender, Context.ConnectionId, negotiationId, candidate);
        if (!route.ShouldForward)
        {
            VoiceDiagnostic("IceIgnoredByServer", callId, sender, route.TargetAccountId,
                negotiationGeneration, peerGeneration, signalId, route.TargetConnectionId);
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: WebRTC ICE candidate ignored by server ({IgnoreReason}).",
                callId, negotiationId, route.IgnoreReason);
            return;
        }
        VoiceDiagnostic("IceForwarded", callId, sender, route.TargetAccountId,
            negotiationGeneration, peerGeneration, signalId, route.TargetConnectionId);
        await Clients.Client(route.TargetConnectionId).SendAsync(VoiceCallHubContract.IceCandidate,
            new WebRtcIceCandidateEvent(callId, sender, negotiationId, negotiationGeneration, peerGeneration, signalId, candidate));
    }

    public async Task<DirectMessageDto> SendDirectMessage(Guid conversationId, SendDirectMessageRequest request)
    {
        var session = await RequireSessionAsync();
        var conversation = await RequireDirectConversationAsync(conversationId, session.AccountId);
        if (request.ClientMessageId == Guid.Empty) throw new HubException("The client message identifier is invalid.");
        if (request.ClientMessageId is { } existingClientId)
        {
            var existing = await db.DirectMessages.Include(value => value.AuthorAccount)
                .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
                .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
                .Include(value => value.Attachments)
                .IncludeForwardedSnapshot()
                .SingleOrDefaultAsync(value => value.AuthorAccountId == session.AccountId &&
                    value.ConversationId == conversationId && value.ClientMessageId == existingClientId);
            if (existing is not null) return DirectMessageMapper.ToDto(existing);
        }
        var attachments = await ValidateAttachmentsAsync(request.AttachmentIds, session.AccountId);
        DirectMessage? reply = null;
        if (request.ReplyToMessageId is { } replyId)
        {
            reply = await db.DirectMessages.Include(value => value.AuthorAccount)
                .Include(value => value.Attachments)
                .SingleOrDefaultAsync(value => value.Id == replyId && value.ConversationId == conversationId);
            if (reply is null || reply.IsDeleted) throw new HubException("The message being replied to is no longer available.");
            if (reply.Kind != MessageKind.User) throw new HubException("System messages cannot be replied to.");
        }
        var message = new DirectMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Conversation = conversation,
            AuthorAccountId = session.AccountId,
            ClientMessageId = request.ClientMessageId,
            AuthorAccount = session.Account,
            Content = ValidContent(request.Content, attachments.Count > 0),
            CreatedAt = DateTimeOffset.UtcNow,
            ReplyToMessageId = reply?.Id,
            ReplyToMessage = reply
        };
        foreach (var attachment in attachments)
        {
            attachment.DirectMessageId = message.Id;
            attachment.DirectMessage = message;
            message.Attachments.Add(attachment);
        }
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
        if (message.Kind != MessageKind.User) throw new HubException("System messages cannot be edited.");
        if (message.AuthorAccountId != accountId) throw new HubException("You can only edit your own messages.");
        if (message.IsDeleted) throw new HubException("Deleted messages cannot be edited.");
        message.Content = ValidContent(request.Content, allowEmpty: message.ForwardedMessageSnapshotId is not null);
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
        if (message.Kind != MessageKind.User) throw new HubException("System messages cannot be deleted.");
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
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
            .Include(value => value.Attachments)
            .IncludeForwardedSnapshot()
            .SingleOrDefaultAsync(value => value.Id == messageId && value.CommunityId == communityId && value.ChannelId == channelId);
        return message ?? throw new HubException("Message not found in this Server channel.");
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
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
            .Include(value => value.Attachments)
            .IncludeForwardedSnapshot()
            .SingleOrDefaultAsync(value => value.Id == messageId && value.ConversationId == conversationId);
        return message ?? throw new HubException("Direct message not found in this conversation.");
    }

    private IClientProxy DirectParticipants(DirectConversation conversation) => Clients.Groups(
        AccountGroup(conversation.ParticipantAAccountId),
        AccountGroup(conversation.ParticipantBAccountId));

    private IClientProxy OtherCallParticipant(CallSessionDto call, Guid accountId) => Clients.Group(
        AccountGroup(call.Participants.Single(value => value.AccountId != accountId).AccountId));

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

    private async Task PublishForumPostAsync(CommunityForumPost post, string change, Guid? actorAccountId = null)
    {
        var accounts = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == post.CommunityId)
            .Select(value => value.AccountId).ToListAsync();
        var owner = await db.Communities.AsNoTracking().Where(value => value.Id == post.CommunityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync();
        if (owner.HasValue) accounts.Add(owner.Value);
        foreach (var accountId in accounts.Distinct())
            if (await authorization.HasChannelPermissionAsync(post.CommunityId, post.ForumChannelId, accountId,
                    CommunityPermission.ViewChannels, db))
                await Clients.Group(AccountGroup(accountId)).SendAsync(CommunityForumHubContract.PostChanged,
                    new CommunityForumPostChangedEvent(post.CommunityId, post.ForumChannelId,
                        CommunityForumEndpoints.ToDto(post), post.Id, change, actorAccountId));
    }

    private async Task RequireChannelAsync(
        Guid communityId, Guid channelId, Guid accountId, CommunityPermission permission)
    {
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, accountId, db);
        if (!access.IsOwner && !await authorization.IsMemberAsync(communityId, accountId, db))
            throw new HubException("You are not a member of this Server.");
        if (!access.Has(permission))
            throw new HubException("You do not have permission to use this Server channel.");
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId &&
                value.Kind == CommunityChannelKind.Text))
            throw new HubException("Text channel not found in this Server.");
    }

    private async Task RequireVoiceChannelAsync(Guid communityId, Guid channelId, Guid accountId,
        CommunityPermission permission)
    {
        var access = await authorization.GetChannelAccessAsync(communityId, channelId, accountId, db);
        if (!access.IsOwner && !await authorization.IsMemberAsync(communityId, accountId, db))
            throw new HubException("You are not a member of this Server.");
        if (!access.Has(CommunityPermission.ViewChannels) || !access.Has(permission))
            throw new HubException("You do not have permission to use this Server voice channel.");
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId &&
                value.Id == channelId && value.Kind == CommunityChannelKind.Voice))
            throw new HubException("Voice channel not found in this Server.");
    }

    private async Task BroadcastVoiceAsync(Guid communityId, string method, object payload)
    {
        var accounts = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == communityId)
            .Select(value => value.AccountId).ToListAsync();
        var owner = await db.Communities.AsNoTracking().Where(value => value.Id == communityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync();
        if (owner.HasValue) accounts.Add(owner.Value);
        var groups = accounts.Distinct().Select(AccountGroup).ToArray();
        if (groups.Length > 0) await Clients.Groups(groups).SendAsync(method, payload);
    }

    private async Task<(IReadOnlyList<CommunityMentionDto> Mentions, HashSet<Guid> Recipients)> ValidateMentionsAsync(
        Guid communityId,
        Guid channelId,
        Guid senderAccountId,
        string content,
        IReadOnlyList<CommunityMentionInput>? requested)
    {
        if (requested is null || requested.Count == 0) return ([], []);
        if (requested.Count > 16) throw new HubException("A message cannot contain more than 16 mention targets.");

        var access = await authorization.GetChannelAccessAsync(communityId, channelId, senderAccountId, db);
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
            if (!MessageText.AllowsMentionAt(content, input.Start)) continue;
            if (!unique.Add((input.Kind, input.TargetId, input.Start))) continue;

            switch (input.Kind)
            {
                case CommunityMentionKind.Account when input.TargetId is { } accountId:
                {
                    if (!memberIds.Contains(accountId)) throw new HubException("Mentioned account is not a member of this Server.");
                    var account = await db.Accounts.AsNoTracking().SingleAsync(value => value.Id == accountId);
                    result.Add(new(input.Kind, accountId, input.Start, input.Length, $"@{account.DisplayName}"));
                    if (CommunityMentionPresentation.ShouldDeliverNotification(senderAccountId, accountId) && await authorization.HasChannelPermissionAsync(
                            communityId, channelId, accountId, CommunityPermission.ViewChannels, db)) recipients.Add(accountId);
                    break;
                }
                case CommunityMentionKind.Role when input.TargetId is { } roleId:
                {
                    var role = roles.SingleOrDefault(value => value.Id == roleId)
                        ?? throw new HubException("Mentioned role does not belong to this Server.");
                    if (!role.IsMentionable && !access.Has(CommunityPermission.MentionEveryone))
                        throw new HubException("You do not have permission to mention that role.");
                    result.Add(new(input.Kind, roleId, input.Start, input.Length, $"@{role.Name.TrimStart('@')}"));
                    var roleMembers = await db.CommunityMemberRoles
                        .Where(value => value.CommunityId == communityId && value.RoleId == roleId)
                        .Select(value => value.AccountId).ToListAsync();
                    foreach (var accountId in roleMembers.Where(value =>
                                 CommunityMentionPresentation.ShouldDeliverNotification(senderAccountId, value)))
                        if (await authorization.HasChannelPermissionAsync(communityId, channelId, accountId, CommunityPermission.ViewChannels, db))
                            recipients.Add(accountId);
                    break;
                }
                case CommunityMentionKind.Everyone:
                    if (!access.Has(CommunityPermission.MentionEveryone))
                        throw new HubException("You do not have permission to mention everyone.");
                    result.Add(new(input.Kind, null, input.Start, input.Length, "@everyone"));
                    foreach (var accountId in memberIds.Where(value =>
                                 CommunityMentionPresentation.ShouldDeliverNotification(senderAccountId, value)))
                        if (await authorization.HasChannelPermissionAsync(communityId, channelId, accountId, CommunityPermission.ViewChannels, db))
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

    private async Task<List<Attachment>> ValidateAttachmentsAsync(IReadOnlyList<Guid>? requested, Guid accountId)
    {
        if (requested is null || requested.Count == 0) return [];
        if (requested.Count > nodeOptions.Value.MaxAttachmentsPerMessage)
            throw new HubException($"Messages may contain at most {nodeOptions.Value.MaxAttachmentsPerMessage} attachments.");
        if (requested.Count != requested.Distinct().Count()) throw new HubException("An attachment was selected more than once.");
        var attachments = await db.Attachments.Where(value => requested.Contains(value.Id)).ToListAsync();
        if (attachments.Count != requested.Count || attachments.Any(value => value.UploaderAccountId != accountId))
            throw new HubException("One or more attachments are unavailable.");
        if (attachments.Any(value => value.ChannelMessageId != null || value.DirectMessageId != null))
            throw new HubException("One or more attachments have already been sent.");
        if (attachments.Any(value => value.OriginalSizeBytes > nodeOptions.Value.MaxAttachmentBytes))
            throw new HubException("One or more attachments exceed this Node's file limit.");
        return requested.Select(id => attachments.Single(value => value.Id == id)).ToList();
    }

    private string ValidContent(string? value, bool allowEmpty = false, Guid? communityId = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (allowEmpty) return string.Empty;
            throw new HubException("Messages cannot be empty.");
        }
        var maximum = limitService.GetEffectiveLimits(communityId).MaxMessageCharacters;
        if (MessageText.CountCharacters(value) > maximum)
            throw new HubException($"Messages cannot exceed {maximum:N0} characters.");
        return value;
    }

    private static string GroupName(Guid communityId, Guid channelId) => $"community:{communityId:N}:channel:{channelId:N}";
    private static string DirectGroup(Guid conversationId) => $"direct:{conversationId:N}";
    internal static string AccountGroup(Guid accountId) => $"account:{accountId:N}";

    private void RequireCommunityMediaRoute(string targetParticipantId)
    {
        if (communityVoiceMedia.Status != CommunityVoiceMediaStatus.Connected)
            throw new HubException("Community voice media is unavailable on this Node.");
        if (string.IsNullOrWhiteSpace(targetParticipantId) || targetParticipantId == Context.ConnectionId)
            throw new HubException("The Community voice media target is invalid.");
        var sourceRoom = communityVoice.RoomFor(Context.ConnectionId)
            ?? throw new HubException("Join a Community voice channel before signaling media.");
        var targetRoom = communityVoice.RoomFor(targetParticipantId);
        if (targetRoom is null || targetRoom.Value != sourceRoom)
            throw new HubException("The media target is not in your Community voice room.");
    }

    private sealed record VoiceStreamAuthorization(Guid AccountId, string DisplayName,
        IReadOnlyList<string> OtherParticipantIds);

    private async Task<VoiceStreamAuthorization> RequireVoiceStreamSessionAsync(
        VoiceMediaSessionKind sessionKind, Guid sessionId, bool publishing)
    {
        if (!Enum.IsDefined(sessionKind)) throw new HubException("That voice session kind is not supported.");
        var session = await RequireSessionAsync();
        if (sessionKind == VoiceMediaSessionKind.DirectCall)
        {
            var call = calls.RequireParticipant(sessionId, session.AccountId, CallState.Active);
            var route = calls.RequireSignalingRoute(sessionId, session.AccountId, Context.ConnectionId,
                CallState.Active);
            return new(session.AccountId, session.Account.DisplayName, [route.TargetConnectionId]);
        }

        var roomKey = communityVoice.RoomFor(Context.ConnectionId)
            ?? throw new HubException("Join a Community voice channel before using its streams.");
        if (roomKey.ChannelId != sessionId)
            throw new HubException("That stream does not belong to your active Community voice channel.");
        if (publishing)
            await RequireVoiceChannelAsync(roomKey.CommunityId, roomKey.ChannelId, session.AccountId,
                CommunityPermission.ShareScreen);
        var room = communityVoice.GetRooms(roomKey.CommunityId)
            .Single(value => value.ChannelId == roomKey.ChannelId);
        return new(session.AccountId, session.Account.DisplayName,
            room.Participants.Where(value => value.ParticipantId != Context.ConnectionId)
                .Select(value => value.ParticipantId).ToArray());
    }

    // TODO: Remove temporary Community voice diagnostics once voice channels are stable.
    private void CommunityVoiceDiagnostic(string eventName, string remoteParticipantId, Guid negotiationId)
    {
        if (!environment.IsDevelopment()) return;
        logger.LogDebug("COMMUNITY VOICE MEDIA Event={Event} LocalParticipant={LocalParticipant} " +
            "RemoteParticipant={RemoteParticipant} NegotiationId={NegotiationId}",
            eventName, Context.ConnectionId, remoteParticipantId, negotiationId);
    }

    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    private void VoiceDiagnostic(string eventName, Guid callId, Guid senderAccountId, Guid? receiverAccountId,
        int? negotiationGeneration, int? peerGeneration, Guid? signalId, string? receiverConnectionId = null)
    {
        if (!environment.IsDevelopment()) return;
        var senderRole = "unknown";
        try
        {
            var call = calls.RequireParticipant(callId, senderAccountId, CallState.Ringing, CallState.Active,
                CallState.Ended, CallState.Rejected, CallState.Cancelled);
            senderRole = call.CallerAccountId == senderAccountId ? "caller" : "callee";
        }
        catch (HubException) { }

        var receiverConnectionIds = receiverAccountId is { } receiver
            ? voiceConnections.ForAccount(receiver)
            : [];
        logger.LogDebug(
            "VOICE TRACE Call={CallId} Account={AccountId} Role={Role} Peer={PeerGeneration} " +
            "Negotiation={NegotiationGeneration} Event={Event} Signal={SignalId} " +
            "ReceiverAccountId={ReceiverAccountId} " +
            "HubConnectionId={ConnectionId} ReceiverConnectionId={ReceiverConnectionId} " +
            "ReceiverConnections={ReceiverConnectionCount} ReceiverConnectionIds={ReceiverConnectionIds}",
            callId, senderAccountId, senderRole, peerGeneration, negotiationGeneration, eventName, signalId,
            receiverAccountId, ShortConnectionId(Context.ConnectionId),
            receiverConnectionId is null ? null : ShortConnectionId(receiverConnectionId), receiverConnectionIds.Count,
            receiverConnectionIds.Select(ShortConnectionId).ToArray());
    }

    private static string ShortConnectionId(string connectionId) =>
        connectionId.Length <= 8 ? connectionId : connectionId[..8];

    private static void ValidateDiagnosticSignal(Guid signalId, int negotiationGeneration, int peerGeneration)
    {
        if (signalId == Guid.Empty) throw new HubException("The WebRTC diagnostic signal identifier is invalid.");
        if (negotiationGeneration <= 0) throw new HubException("The WebRTC negotiation generation is invalid.");
        if (peerGeneration <= 0) throw new HubException("The WebRTC peer generation is invalid.");
    }
}
