using Iridium.Protocol;
using Iridium.Server.Domain;
using System.Text.Json;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class ChannelMessageMapper
{
    public static ChannelMessageDto ToDto(ChannelMessage message)
    {
        MessageReplyDto? reply = null;
        if (message.ReplyToMessage is { } original)
        {
            reply = new MessageReplyDto(
                original.Id,
                original.AuthorAccountId,
                original.AuthorDisplayNameSnapshot ?? original.AuthorAccount.DisplayName,
                original.IsDeleted ? null : Excerpt(original.Content),
                original.IsDeleted,
                original.IsDeleted ? null : AttachmentSummary(original.Attachments),
                AvatarRevision: original.AuthorAvatarRevisionSnapshot ?? original.AuthorAccount.AvatarRevision,
                AvatarSnapshotMessageId: original.AuthorAvatarObjectKeySnapshot is null ? null : original.Id,
                HasHistoricalSnapshot: original.AuthorDisplayNameSnapshot is not null);
        }

        return new ChannelMessageDto(
            message.Id,
            message.CommunityId,
            message.ChannelId,
            new MessageAuthorDto(message.AuthorAccountId, message.AuthorAccount.Username,
                message.AuthorDisplayNameSnapshot ?? message.AuthorAccount.DisplayName,
                AvatarRevision: message.AuthorAvatarRevisionSnapshot ?? message.AuthorAccount.AvatarRevision,
                AvatarSnapshotMessageId: message.AuthorAvatarObjectKeySnapshot is null ? null : message.Id,
                HasHistoricalSnapshot: message.AuthorDisplayNameSnapshot is not null),
            message.IsDeleted ? string.Empty : message.Content,
            message.CreatedAt,
            message.EditedAt,
            message.IsDeleted,
            reply,
            DeserializeMentions(message.MentionsJson),
            message.ClientMessageId,
            Attachments: message.IsDeleted ? [] : message.Attachments.Select(ToAttachment).ToArray());
    }

    internal static AttachmentDto ToAttachment(Attachment value) => new(
        value.Id, value.OriginalFileName, value.OriginalContentType, value.OriginalSizeBytes,
        $"api/attachments/{value.Id}", value.Width, value.Height, value.AverageColor, IsSpoiler: value.IsSpoiler,
        PreviewDownloadUrl: value.PreviewObjectKey is null ? null : $"api/attachments/{value.Id}/preview",
        PreviewContentType: value.PreviewContentType, PreviewSizeBytes: value.PreviewSizeBytes);

    private static string Excerpt(string content)
        => content;

    internal static string? AttachmentSummary(IEnumerable<Attachment> attachments) =>
        attachments.FirstOrDefault(value => value.OriginalContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase)) is { } video
            ? $"Video attachment: {video.OriginalFileName}"
            : null;

    internal static IReadOnlyList<CommunityMentionDto> DeserializeMentions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<CommunityMentionDto>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static async Task<IReadOnlyList<ChannelMessageDto>> ResolveMentionNamesAsync(
        IReadOnlyList<ChannelMessageDto> messages, IridiumDbContext db)
    {
        var mentions = messages.SelectMany(value => value.Mentions ?? []).ToArray();
        if (mentions.Length == 0) return messages;
        var accountIds = mentions.Where(value => value.Kind == CommunityMentionKind.Account && value.TargetId.HasValue)
            .Select(value => value.TargetId!.Value).Distinct().ToArray();
        var roleIds = mentions.Where(value => value.Kind == CommunityMentionKind.Role && value.TargetId.HasValue)
            .Select(value => value.TargetId!.Value).Distinct().ToArray();
        var accountNames = await db.Accounts.AsNoTracking().Where(value => accountIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.DisplayName);
        var communityIds = messages.Select(value => value.CommunityId).Distinct().ToArray();
        var roleNames = await db.CommunityRoles.AsNoTracking()
            .Where(value => communityIds.Contains(value.CommunityId) && roleIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.Name);
        return messages.Select(message => message with
        {
            Mentions = message.Mentions?.Select(mention => mention with
            {
                DisplayText = mention.Kind switch
                {
                    CommunityMentionKind.Account when mention.TargetId is { } id && accountNames.TryGetValue(id, out var name) => $"@{name}",
                    CommunityMentionKind.Role when mention.TargetId is { } id && roleNames.TryGetValue(id, out var name) => $"@{name.TrimStart('@')}",
                    CommunityMentionKind.Everyone => "@everyone",
                    _ => mention.DisplayText
                }
            }).ToArray()
        }).ToArray();
    }

    // Message presentation is already canonical here: snapshots are immutable and
    // null snapshots deliberately retain the account-default identity from ToDto.
    internal static Task<IReadOnlyList<ChannelMessageDto>> ResolveCommunityProfilesAsync(
        IReadOnlyList<ChannelMessageDto> messages, IridiumDbContext _) => Task.FromResult(messages);

    internal static async Task<ChannelMessageDto> ResolveCommunityProfileAsync(
        ChannelMessageDto message, IridiumDbContext db) =>
        (await ResolveCommunityProfilesAsync([message], db))[0];

    internal static string ResolveDisplayName(CommunityMember member) =>
        !string.IsNullOrWhiteSpace(member.Nickname) ? member.Nickname :
        !string.IsNullOrWhiteSpace(ValidPreset(member)?.DisplayName) ? ValidPreset(member)!.DisplayName! :
        member.Account.DisplayName;

    internal static UserProfilePreset? ValidPreset(CommunityMember member) =>
        member.ProfilePreset is { } preset && preset.AccountId == member.AccountId &&
        preset.CommunityId == member.CommunityId ? preset : null;
}
