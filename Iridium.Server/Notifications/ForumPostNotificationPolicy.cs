using Iridium.Protocol;
using Iridium.Server.Domain;

namespace Iridium.Server.Notifications;

public static class ForumPostNotificationPolicy
{
    public static bool DeliversOrdinaryReply(ForumPostSubscription? subscription) =>
        subscription?.NotificationLevel == ForumPostNotificationLevel.AllMessages;

    public static bool DeliversMention(ForumPostSubscription? subscription) =>
        subscription?.NotificationLevel != ForumPostNotificationLevel.Muted;
}
