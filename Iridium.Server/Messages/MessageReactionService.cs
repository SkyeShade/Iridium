using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Messages;

public sealed class MessageReactionService(
    IridiumDbContext db,
    CommunityAuthorizationService authorization)
{
    public async Task<MessageReactionChangedEvent> AddAsync(Guid messageId, Guid accountId,
        ReactionEmojiRequest request, CancellationToken cancellationToken = default)
    {
        var message = await RequireMessageAsync(messageId, accountId, cancellationToken);
        var identity = await ResolveForUseAsync(message.CommunityId, message.ChannelId, accountId, request,
            cancellationToken);
        var existing = await db.MessageReactions.SingleOrDefaultAsync(value => value.MessageId == messageId &&
            value.AccountId == accountId && value.EmojiKey == identity.Key, cancellationToken);
        if (existing is not null)
            return await ChangedAsync(message, existing, accountId, true, cancellationToken);

        var groupExists = await db.MessageReactions.AnyAsync(value => value.MessageId == messageId &&
            value.EmojiKey == identity.Key, cancellationToken);
        if (!groupExists)
        {
            if (!await authorization.HasChannelPermissionAsync(message.CommunityId, message.ChannelId, accountId,
                    CommunityPermission.AddReactions, db))
                throw new HubException("You do not have permission to add a new reaction to this message.");
            var distinct = await db.MessageReactions.Where(value => value.MessageId == messageId)
                .Select(value => value.EmojiKey).Distinct().CountAsync(cancellationToken);
            if (distinct >= MessageReactionLimits.MaximumDistinctPerMessage)
                throw new HubException($"Messages may have at most {MessageReactionLimits.MaximumDistinctPerMessage} different reactions.");
        }

        var row = identity.ToRow(message, accountId);
        db.MessageReactions.Add(row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (!await db.MessageReactions.AnyAsync(value => value.MessageId == messageId &&
                    value.AccountId == accountId && value.EmojiKey == identity.Key, cancellationToken)) throw;
        }
        return await ChangedAsync(message, row, accountId, true, cancellationToken);
    }

    public async Task<MessageReactionChangedEvent> RemoveAsync(Guid messageId, Guid actorAccountId,
        ReactionEmojiRequest request, Guid? targetAccountId = null, CancellationToken cancellationToken = default)
    {
        var message = await RequireMessageAsync(messageId, actorAccountId, cancellationToken);
        var key = IdentityKey(request);
        var target = targetAccountId ?? actorAccountId;
        if (target != actorAccountId && !await authorization.HasChannelPermissionAsync(message.CommunityId,
                message.ChannelId, actorAccountId, CommunityPermission.ManageMessages, db))
            throw new HubException("You do not have permission to remove another member's reaction.");
        var row = await db.MessageReactions.SingleOrDefaultAsync(value => value.MessageId == messageId &&
            value.AccountId == target && value.EmojiKey == key, cancellationToken)
            ?? throw new HubException("That reaction is no longer present.");
        var emoji = await ToDtoAsync(row, cancellationToken);
        db.MessageReactions.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.MessageReactions.CountAsync(value => value.MessageId == messageId &&
            value.EmojiKey == key, cancellationToken);
        return new(message.CommunityId, message.ChannelId, message.Id, emoji, count, target, false);
    }

    public async Task<DirectMessageReactionChangedEvent> AddDirectAsync(Guid messageId, Guid accountId,
        ReactionEmojiRequest request, CancellationToken cancellationToken = default)
    {
        var message = await RequireDirectMessageAsync(messageId, accountId, cancellationToken);
        var identity = await ResolveForDirectUseAsync(accountId, request, cancellationToken);
        var existing = await db.DirectMessageReactions.SingleOrDefaultAsync(value => value.MessageId == messageId &&
            value.AccountId == accountId && value.EmojiKey == identity.Key, cancellationToken);
        if (existing is not null)
            return await DirectChangedAsync(message, existing, accountId, true, cancellationToken);

        if (!await db.DirectMessageReactions.AnyAsync(value => value.MessageId == messageId &&
                value.EmojiKey == identity.Key, cancellationToken))
        {
            var distinct = await db.DirectMessageReactions.Where(value => value.MessageId == messageId)
                .Select(value => value.EmojiKey).Distinct().CountAsync(cancellationToken);
            if (distinct >= MessageReactionLimits.MaximumDistinctPerMessage)
                throw new HubException($"Messages may have at most {MessageReactionLimits.MaximumDistinctPerMessage} different reactions.");
        }

        var row = identity.ToDirectRow(message, accountId);
        db.DirectMessageReactions.Add(row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (!await db.DirectMessageReactions.AnyAsync(value => value.MessageId == messageId &&
                    value.AccountId == accountId && value.EmojiKey == identity.Key, cancellationToken)) throw;
        }
        return await DirectChangedAsync(message, row, accountId, true, cancellationToken);
    }

    public async Task<DirectMessageReactionChangedEvent> RemoveDirectAsync(Guid messageId, Guid accountId,
        ReactionEmojiRequest request, CancellationToken cancellationToken = default)
    {
        var message = await RequireDirectMessageAsync(messageId, accountId, cancellationToken);
        var key = IdentityKey(request);
        var row = await db.DirectMessageReactions.SingleOrDefaultAsync(value => value.MessageId == messageId &&
            value.AccountId == accountId && value.EmojiKey == key, cancellationToken)
            ?? throw new HubException("That reaction is no longer present.");
        var emoji = await ToDtoAsync(row, cancellationToken);
        db.DirectMessageReactions.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.DirectMessageReactions.CountAsync(value => value.MessageId == messageId &&
            value.EmojiKey == key, cancellationToken);
        return new(message.ConversationId, message.Id, emoji, count, accountId, false);
    }

    public async Task<IReadOnlyList<ChannelMessageDto>> AttachSummariesAsync(
        IReadOnlyList<ChannelMessageDto> messages, Guid currentAccountId,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) return messages;
        var ids = messages.Select(value => value.Id).ToArray();
        var rows = await db.MessageReactions.AsNoTracking().Where(value => ids.Contains(value.MessageId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return messages.Select(value => value with { Reactions = [] }).ToArray();
        var customIds = rows.Where(value => value.CustomEmojiId.HasValue).Select(value => value.CustomEmojiId!.Value)
            .Distinct().ToArray();
        var live = await db.CommunityEmojis.AsNoTracking().Where(value => customIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        var summaries = rows.GroupBy(value => value.MessageId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<ReactionSummaryDto>)group.GroupBy(value => value.EmojiKey)
                .OrderBy(value => value.Min(row => row.CreatedAt)).Select(reaction =>
                {
                    var sample = reaction.First();
                    return new ReactionSummaryDto(ToDto(sample, live), reaction.Count(),
                        reaction.Any(value => value.AccountId == currentAccountId));
                }).ToArray());
        return messages.Select(message => message with
        {
            Reactions = summaries.GetValueOrDefault(message.Id) ?? []
        }).ToArray();
    }

    public async Task<IReadOnlyList<DirectMessageDto>> AttachDirectSummariesAsync(
        IReadOnlyList<DirectMessageDto> messages, Guid currentAccountId,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) return messages;
        var ids = messages.Select(value => value.Id).ToArray();
        var rows = await db.DirectMessageReactions.AsNoTracking().Where(value => ids.Contains(value.MessageId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return messages.Select(value => value with { Reactions = [] }).ToArray();
        var customIds = rows.Where(value => value.CustomEmojiId.HasValue).Select(value => value.CustomEmojiId!.Value)
            .Distinct().ToArray();
        var live = await db.CommunityEmojis.AsNoTracking().Where(value => customIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        var summaries = rows.GroupBy(value => value.MessageId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<ReactionSummaryDto>)group.GroupBy(value => value.EmojiKey)
                .OrderBy(value => value.Min(row => row.CreatedAt)).Select(reaction =>
                {
                    var sample = reaction.First();
                    return new ReactionSummaryDto(ToDto(sample, live), reaction.Count(),
                        reaction.Any(value => value.AccountId == currentAccountId));
                }).ToArray());
        return messages.Select(message => message with
        {
            Reactions = summaries.GetValueOrDefault(message.Id) ?? []
        }).ToArray();
    }

    public async Task<ReactionDetailsDto> DetailsAsync(Guid messageId, Guid accountId,
        ReactionEmojiRequest request, Guid? afterAccountId, int? limit,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireMessageAsync(messageId, accountId, cancellationToken);
        var key = IdentityKey(request);
        var take = Math.Clamp(limit ?? MessageReactionLimits.ReactorPageSize, 1,
            MessageReactionLimits.MaximumReactorPageSize);
        var query = db.MessageReactions.AsNoTracking().Where(value => value.MessageId == messageId &&
            value.EmojiKey == key);
        var count = await query.CountAsync(cancellationToken);
        if (afterAccountId is { } after) query = query.Where(value => value.AccountId.CompareTo(after) > 0);
        var rows = await query.Include(value => value.Account).OrderBy(value => value.AccountId).Take(take + 1)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) throw new KeyNotFoundException("That reaction is no longer present.");
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var emoji = await ToDtoAsync(rows[0], cancellationToken);
        return new(emoji, count, rows.Select(value => new ReactionUserDto(value.AccountId,
            value.Account.DisplayName, value.Account.BaseAvatarPresetId, value.Account.AvatarRevision)).ToArray(),
            hasMore ? rows[^1].AccountId.ToString("N") : null);
    }

    public async Task<ReactionDetailsDto> DirectDetailsAsync(Guid messageId, Guid accountId,
        ReactionEmojiRequest request, Guid? afterAccountId, int? limit,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireDirectMessageAsync(messageId, accountId, cancellationToken);
        var key = IdentityKey(request);
        var take = Math.Clamp(limit ?? MessageReactionLimits.ReactorPageSize, 1,
            MessageReactionLimits.MaximumReactorPageSize);
        var query = db.DirectMessageReactions.AsNoTracking().Where(value => value.MessageId == messageId &&
            value.EmojiKey == key);
        var count = await query.CountAsync(cancellationToken);
        if (afterAccountId is { } after) query = query.Where(value => value.AccountId.CompareTo(after) > 0);
        var rows = await query.Include(value => value.Account).OrderBy(value => value.AccountId).Take(take + 1)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) throw new KeyNotFoundException("That reaction is no longer present.");
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var emoji = await ToDtoAsync(rows[0], cancellationToken);
        return new(emoji, count, rows.Select(value => new ReactionUserDto(value.AccountId,
            value.Account.DisplayName, value.Account.BaseAvatarPresetId, value.Account.AvatarRevision)).ToArray(),
            hasMore ? rows[^1].AccountId.ToString("N") : null);
    }

    private async Task<ChannelMessage> RequireMessageAsync(Guid messageId, Guid accountId,
        CancellationToken cancellationToken)
    {
        var message = await db.ChannelMessages.SingleOrDefaultAsync(value => value.Id == messageId,
            cancellationToken);
        if (message is null || message.IsDeleted) throw new KeyNotFoundException("That message is unavailable.");
        var access = await authorization.GetChannelAccessAsync(message.CommunityId, message.ChannelId, accountId, db);
        if (!access.Has(CommunityPermission.ViewChannels) || !access.Has(CommunityPermission.ReadMessageHistory))
            throw new UnauthorizedAccessException("You cannot react to messages in that channel.");
        return message;
    }

    private async Task<DirectMessage> RequireDirectMessageAsync(Guid messageId, Guid accountId,
        CancellationToken cancellationToken)
    {
        var message = await db.DirectMessages.Include(value => value.Conversation)
            .SingleOrDefaultAsync(value => value.Id == messageId, cancellationToken);
        if (message is null || message.IsDeleted) throw new KeyNotFoundException("That message is unavailable.");
        if (message.Conversation.ParticipantAAccountId != accountId &&
            message.Conversation.ParticipantBAccountId != accountId)
            throw new UnauthorizedAccessException("You cannot react to messages in that Direct Message.");
        return message;
    }

    private async Task<ResolvedIdentity> ResolveForUseAsync(Guid targetCommunityId, Guid targetChannelId,
        Guid accountId,
        ReactionEmojiRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind == ReactionEmojiKind.Standard)
        {
            var standard = StandardEmojiCatalog.All.SingleOrDefault(value =>
                string.Equals(value.Glyph, request.StandardEmojiValue, StringComparison.Ordinal));
            if (standard is null) throw new HubException("That standard emoji is not supported.");
            return new($"s:{standard.ArtworkKey}", standard.Glyph, null, null);
        }
        if (request.CustomEmojiId is not { } customId) throw new HubException("Choose a valid custom emoji.");
        var custom = await db.CommunityEmojis.AsNoTracking().SingleOrDefaultAsync(value => value.Id == customId,
            cancellationToken) ?? throw new HubException("That custom emoji is no longer available.");
        if (!await authorization.IsMemberAsync(custom.CommunityId, accountId, db))
            throw new HubException("That custom emoji is not available to your account.");
        if (custom.CommunityId != targetCommunityId && !await authorization.HasChannelPermissionAsync(
                targetCommunityId, targetChannelId, accountId, CommunityPermission.UseExternalEmoji, db))
            throw new HubException("You do not have permission to use custom emoji from another Server.");
        return new($"c:{custom.Id:N}", null, custom, custom.Id);
    }

    private async Task<ResolvedIdentity> ResolveForDirectUseAsync(Guid accountId, ReactionEmojiRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind == ReactionEmojiKind.Standard)
        {
            var standard = StandardEmojiCatalog.All.SingleOrDefault(value =>
                string.Equals(value.Glyph, request.StandardEmojiValue, StringComparison.Ordinal));
            if (standard is null) throw new HubException("That standard emoji is not supported.");
            return new($"s:{standard.ArtworkKey}", standard.Glyph, null, null);
        }
        if (request.CustomEmojiId is not { } customId) throw new HubException("Choose a valid custom emoji.");
        var custom = await db.CommunityEmojis.AsNoTracking().SingleOrDefaultAsync(value => value.Id == customId,
            cancellationToken) ?? throw new HubException("That custom emoji is no longer available.");
        if (!await authorization.IsMemberAsync(custom.CommunityId, accountId, db))
            throw new HubException("That custom emoji is not available to your account.");
        return new($"c:{custom.Id:N}", null, custom, custom.Id);
    }

    internal static string IdentityKey(ReactionEmojiRequest request)
    {
        if (request.Kind == ReactionEmojiKind.Custom && request.CustomEmojiId is { } customId)
            return $"c:{customId:N}";
        if (request.Kind == ReactionEmojiKind.Standard && !string.IsNullOrEmpty(request.StandardEmojiValue) &&
            StandardEmojiCatalog.All.SingleOrDefault(value => value.Glyph == request.StandardEmojiValue) is { } standard)
            return $"s:{standard.ArtworkKey}";
        throw new HubException("That reaction emoji is invalid.");
    }

    private async Task<MessageReactionChangedEvent> ChangedAsync(ChannelMessage message, MessageReaction row,
        Guid accountId, bool added, CancellationToken cancellationToken)
    {
        var count = await db.MessageReactions.CountAsync(value => value.MessageId == message.Id &&
            value.EmojiKey == row.EmojiKey, cancellationToken);
        return new(message.CommunityId, message.ChannelId, message.Id, await ToDtoAsync(row, cancellationToken),
            count, accountId, added);
    }

    private async Task<ReactionEmojiDto> ToDtoAsync(MessageReaction row, CancellationToken cancellationToken)
    {
        Dictionary<Guid, CommunityEmoji> live = [];
        if (row.CustomEmojiId is { } id && await db.CommunityEmojis.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == id, cancellationToken) is { } emoji) live[id] = emoji;
        return ToDto(row, live);
    }

    private async Task<ReactionEmojiDto> ToDtoAsync(DirectMessageReaction row,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, CommunityEmoji> live = [];
        if (row.CustomEmojiId is { } id && await db.CommunityEmojis.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == id, cancellationToken) is { } emoji) live[id] = emoji;
        return ToDto(row, live);
    }

    private static ReactionEmojiDto ToDto(MessageReaction row, IReadOnlyDictionary<Guid, CommunityEmoji> live)
    {
        if (row.EmojiKind == ReactionEmojiKind.Standard)
        {
            var standard = StandardEmojiCatalog.All.SingleOrDefault(value => value.Glyph == row.StandardEmojiValue);
            return new(ReactionEmojiKind.Standard, row.StandardEmojiValue, standard?.ArtworkKey,
                CustomEmojiAvailable: true);
        }
        CommunityEmoji? custom = null;
        var available = row.CustomEmojiId is { } id && live.TryGetValue(id, out custom);
        return new(ReactionEmojiKind.Custom, CustomEmojiId: row.CustomEmojiId,
            CustomEmojiName: available ? custom!.Name : row.CustomEmojiNameSnapshot,
            CustomEmojiContentType: available ? custom!.ContentType : row.CustomEmojiContentTypeSnapshot,
            CustomEmojiAnimated: available ? custom!.IsAnimated : row.CustomEmojiAnimatedSnapshot,
            CustomEmojiWidth: available ? custom!.Width : row.CustomEmojiWidthSnapshot,
            CustomEmojiHeight: available ? custom!.Height : row.CustomEmojiHeightSnapshot,
            CustomEmojiRevision: available ? custom!.Revision : row.CustomEmojiRevisionSnapshot,
            CustomEmojiAvailable: available);
    }

    private static ReactionEmojiDto ToDto(DirectMessageReaction row,
        IReadOnlyDictionary<Guid, CommunityEmoji> live)
    {
        if (row.EmojiKind == ReactionEmojiKind.Standard)
        {
            var standard = StandardEmojiCatalog.All.SingleOrDefault(value => value.Glyph == row.StandardEmojiValue);
            return new(ReactionEmojiKind.Standard, row.StandardEmojiValue, standard?.ArtworkKey,
                CustomEmojiAvailable: true);
        }
        CommunityEmoji? custom = null;
        var available = row.CustomEmojiId is { } id && live.TryGetValue(id, out custom);
        return new(ReactionEmojiKind.Custom, CustomEmojiId: row.CustomEmojiId,
            CustomEmojiName: available ? custom!.Name : row.CustomEmojiNameSnapshot,
            CustomEmojiContentType: available ? custom!.ContentType : row.CustomEmojiContentTypeSnapshot,
            CustomEmojiAnimated: available ? custom!.IsAnimated : row.CustomEmojiAnimatedSnapshot,
            CustomEmojiWidth: available ? custom!.Width : row.CustomEmojiWidthSnapshot,
            CustomEmojiHeight: available ? custom!.Height : row.CustomEmojiHeightSnapshot,
            CustomEmojiRevision: available ? custom!.Revision : row.CustomEmojiRevisionSnapshot,
            CustomEmojiAvailable: available);
    }

    private async Task<DirectMessageReactionChangedEvent> DirectChangedAsync(DirectMessage message,
        DirectMessageReaction row, Guid accountId, bool added, CancellationToken cancellationToken)
    {
        var count = await db.DirectMessageReactions.CountAsync(value => value.MessageId == message.Id &&
            value.EmojiKey == row.EmojiKey, cancellationToken);
        return new(message.ConversationId, message.Id, await ToDtoAsync(row, cancellationToken), count,
            accountId, added);
    }

    private sealed record ResolvedIdentity(string Key, string? StandardValue, CommunityEmoji? Custom, Guid? CustomId)
    {
        public MessageReaction ToRow(ChannelMessage message, Guid accountId) => new()
        {
            MessageId = message.Id, Message = message, AccountId = accountId, Account = null!, EmojiKey = Key,
            EmojiKind = Custom is null ? ReactionEmojiKind.Standard : ReactionEmojiKind.Custom,
            StandardEmojiValue = StandardValue, CustomEmojiId = CustomId,
            CustomEmojiNameSnapshot = Custom?.Name, CustomEmojiContentTypeSnapshot = Custom?.ContentType,
            CustomEmojiAnimatedSnapshot = Custom?.IsAnimated ?? false,
            CustomEmojiWidthSnapshot = Custom?.Width ?? 0, CustomEmojiHeightSnapshot = Custom?.Height ?? 0,
            CustomEmojiRevisionSnapshot = Custom?.Revision ?? 0, CreatedAt = DateTimeOffset.UtcNow
        };

        public DirectMessageReaction ToDirectRow(DirectMessage message, Guid accountId) => new()
        {
            MessageId = message.Id, Message = message, AccountId = accountId, Account = null!, EmojiKey = Key,
            EmojiKind = Custom is null ? ReactionEmojiKind.Standard : ReactionEmojiKind.Custom,
            StandardEmojiValue = StandardValue, CustomEmojiId = CustomId,
            CustomEmojiNameSnapshot = Custom?.Name, CustomEmojiContentTypeSnapshot = Custom?.ContentType,
            CustomEmojiAnimatedSnapshot = Custom?.IsAnimated ?? false,
            CustomEmojiWidthSnapshot = Custom?.Width ?? 0, CustomEmojiHeightSnapshot = Custom?.Height ?? 0,
            CustomEmojiRevisionSnapshot = Custom?.Revision ?? 0, CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
