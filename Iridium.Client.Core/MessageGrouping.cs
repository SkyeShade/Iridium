using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class MessageGrouping
{
    public static bool StartsNewGroup(ChannelMessageDto? previous, ChannelMessageDto current)
    {
        if (previous is null) return true;
        if (previous.Kind != MessageKind.User || current.Kind != MessageKind.User) return true;
        if (previous.Author.AccountId != current.Author.AccountId) return true;
        if (previous.Author.HasHistoricalSnapshot || current.Author.HasHistoricalSnapshot)
        {
            if (previous.Author.HasHistoricalSnapshot != current.Author.HasHistoricalSnapshot) return true;
            if (!string.Equals(previous.Author.DisplayName, current.Author.DisplayName, StringComparison.Ordinal)) return true;
            if (previous.Author.AvatarRevision != current.Author.AvatarRevision) return true;
            if (previous.Author.AvatarSnapshotMessageId.HasValue != current.Author.AvatarSnapshotMessageId.HasValue) return true;
        }
        if (previous.IsDeleted || current.IsDeleted) return true;
        if (current.ReplyTo is not null) return true;
        return current.CreatedAt - previous.CreatedAt > TimeSpan.FromMinutes(1);
    }
}
