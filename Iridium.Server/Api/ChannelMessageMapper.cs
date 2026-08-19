using Iridium.Protocol;
using Iridium.Server.Domain;
using System.Text.Json;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

internal static class ChannelMessageMapper
{
    public static ChannelMessageDto ToDto(ChannelMessage message)
    {
        MessageReplyDto? reply = null;
        if (message.ReplyToMessage is { } original)
        {
            reply = new MessageReplyDto(
                original.Id,
                original.AuthorAccountId,
                original.AuthorAccount.DisplayName,
                original.IsDeleted ? null : Excerpt(original.Content),
                original.IsDeleted);
        }

        return new ChannelMessageDto(
            message.Id,
            message.CommunityId,
            message.ChannelId,
            new MessageAuthorDto(message.AuthorAccountId, message.AuthorAccount.Username, message.AuthorAccount.DisplayName),
            message.IsDeleted ? string.Empty : message.Content,
            message.CreatedAt,
            message.EditedAt,
            message.IsDeleted,
            reply,
            DeserializeMentions(message.MentionsJson));
    }

    private static string Excerpt(string content)
    {
        var singleLine = string.Join(' ', content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine;
    }

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
}
