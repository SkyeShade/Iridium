using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class FriendRequestNotificationTests
{
    [Fact]
    public void NoPendingRequestsProducesNoNotification()
    {
        Assert.Equal(FriendRequestNotificationLocation.None,
            FriendRequestNotifications.Route(0, outsideHome: true, friendsActive: false));
    }

    [Theory]
    [InlineData("server", 1, true, false, FriendRequestNotificationLocation.Home)]
    [InlineData("dm", 9, false, false, FriendRequestNotificationLocation.Friends)]
    [InlineData("message-requests", 10, false, false, FriendRequestNotificationLocation.Friends)]
    [InlineData("friends-online", 1, false, true, FriendRequestNotificationLocation.Pending)]
    [InlineData("friends-all", 9, false, true, FriendRequestNotificationLocation.Pending)]
    [InlineData("friends-pending", 10, false, true, FriendRequestNotificationLocation.Pending)]
    public void PendingRequestUsesExactlyOneDeepestNavigationLocation(
        string navigationState, int count, bool outsideHome, bool friendsActive,
        FriendRequestNotificationLocation expected)
    {
        Assert.False(string.IsNullOrWhiteSpace(navigationState));
        Assert.Equal(expected, FriendRequestNotifications.Route(count, outsideHome, friendsActive));
    }

    [Fact]
    public void IncomingCountTracksRealtimeShapedFriendCollections()
    {
        var incoming = Friend(FriendshipStatus.Pending, outgoing: false);
        var outgoing = Friend(FriendshipStatus.Pending, outgoing: true);
        var accepted = Friend(FriendshipStatus.Accepted, outgoing: false);

        Assert.Equal(0, FriendRequestNotifications.IncomingCount([]));
        Assert.Equal(1, FriendRequestNotifications.IncomingCount([incoming, outgoing, accepted]));
        Assert.Equal(0, FriendRequestNotifications.IncomingCount([outgoing, accepted]));
    }

    private static FriendDto Friend(FriendshipStatus status, bool outgoing) => new(
        Guid.NewGuid(), Guid.NewGuid(), "friend", "Friend", null, null, status, outgoing, PublicPresence.Offline);
}
