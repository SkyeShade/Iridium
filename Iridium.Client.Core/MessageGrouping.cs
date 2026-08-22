using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class MessageGrouping
{
    public static bool StartsNewGroup(ChannelMessageDto? previous, ChannelMessageDto current)
    {
        if (previous is null) return true;
        if (previous.Kind != MessageKind.User || current.Kind != MessageKind.User) return true;
        if (previous.Author.AccountId != current.Author.AccountId) return true;
        if (previous.IsDeleted || current.IsDeleted) return true;
        if (current.ReplyTo is not null) return true;
        return current.CreatedAt - previous.CreatedAt > TimeSpan.FromMinutes(1);
    }
}
