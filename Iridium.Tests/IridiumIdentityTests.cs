using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class IridiumIdentityTests
{
    [Theory]
    [InlineData("skye@friends.example", "skye", "friends.example")]
    [InlineData("Skye@LOCALHOST:5159", "skye", "localhost:5159")]
    public void ParsesAndCanonicallyFormatsIdentity(string value, string username, string authority)
    {
        Assert.True(IridiumIdentity.TryParse(value, out var identity));
        Assert.Equal(username, identity.Username);
        Assert.Equal(authority, identity.NodeAuthority);
        Assert.Equal($"{username}@{authority}", identity.ToString());
    }

    [Theory]
    [InlineData("@friends.example")]
    [InlineData("skye")]
    [InlineData("skye@@friends.example")]
    public void RejectsIncompleteIdentity(string value) =>
        Assert.False(IridiumIdentity.TryParse(value, out _));
}
