using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class MessageTimeline
{
    public static IReadOnlyList<ChannelMessageDto> Visible(IReadOnlyList<ChannelMessageDto> messages) =>
        messages.Where(message => !message.IsDeleted).ToArray();

    public static ChannelMessageDto? LatestEditableOwn(IReadOnlyList<ChannelMessageDto> messages, Guid accountId) =>
        messages.LastOrDefault(message => !message.IsDeleted && message.Kind == MessageKind.User &&
            message.DeliveryState == MessageDeliveryState.Confirmed && message.Author.AccountId == accountId);

    public static void ApplyDeletion(List<ChannelMessageDto> messages, Guid messageId)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].ReplyTo?.MessageId != messageId) continue;
            messages[index] = messages[index] with
            {
                ReplyTo = messages[index].ReplyTo! with { Excerpt = null, IsDeleted = true }
            };
        }
        messages.RemoveAll(message => message.Id == messageId);
    }

    public static void ApplyDeletion(List<DirectMessageDto> messages, Guid messageId)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].ReplyTo?.MessageId != messageId) continue;
            messages[index] = messages[index] with
            {
                ReplyTo = messages[index].ReplyTo! with { Excerpt = null, IsDeleted = true }
            };
        }
        messages.RemoveAll(message => message.Id == messageId);
    }
}
