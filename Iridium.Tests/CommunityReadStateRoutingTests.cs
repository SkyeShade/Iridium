using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class CommunityReadStateRoutingTests
{
    [Theory]
    [InlineData(CommunityChannelKind.Text, true)]
    [InlineData(CommunityChannelKind.Forum, false)]
    [InlineData(CommunityChannelKind.Voice, false)]
    public void GenericChannelReadEndpointOnlyAcceptsMessageChannels(CommunityChannelKind kind, bool expected) =>
        Assert.Equal(expected, CommunityReadStateRouting.SupportsChannelReadEndpoint(kind));
}
