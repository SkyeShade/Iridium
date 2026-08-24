namespace Iridium.Client.Core;

public enum FriendRequestNotificationLocation
{
    None,
    Home,
    Friends,
    Pending
}

public static class FriendRequestNotifications
{
    public static int IncomingCount(IEnumerable<Iridium.Protocol.FriendDto> friends) => friends.Count(friend =>
        friend.Status == Iridium.Protocol.FriendshipStatus.Pending && !friend.IsOutgoing);

    public static FriendRequestNotificationLocation Route(
        int incomingPendingCount, bool outsideHome, bool friendsActive)
    {
        if (incomingPendingCount <= 0) return FriendRequestNotificationLocation.None;
        if (outsideHome) return FriendRequestNotificationLocation.Home;
        return friendsActive
            ? FriendRequestNotificationLocation.Pending
            : FriendRequestNotificationLocation.Friends;
    }
}
