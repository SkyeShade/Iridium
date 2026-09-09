using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class CommunityReadStateRouting
{
    public static bool SupportsChannelReadEndpoint(CommunityChannelKind kind) =>
        kind == CommunityChannelKind.Text;
}
