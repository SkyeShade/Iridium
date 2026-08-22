using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class DirectNotificationProjection
{
    public static IReadOnlyList<DirectConversationDto> Build(
        IReadOnlyList<DirectConversationDto> conversations, Guid? ringingCallerAccountId) => conversations
        .Where(value => value.UnreadCount > 0 || value.OtherParticipant.AccountId == ringingCallerAccountId)
        .GroupBy(value => value.Id)
        .Select(group => group.OrderByDescending(value => value.LastMessageAt).First())
        .OrderByDescending(value => value.OtherParticipant.AccountId == ringingCallerAccountId)
        .ThenByDescending(value => value.LastMessageAt)
        .ToArray();
}
