using Iridium.Protocol;

namespace Iridium.Client.Core;

public enum MobileDirectCallPhase
{
    Calling,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

public sealed record MobileDirectCallBarProjection(
    MobileDirectCallPhase Phase,
    string Status,
    bool CanUseMediaControls,
    bool IsPreAnswer);

public static class MobileDirectCallBarState
{
    public static bool ShouldShow(CallSessionDto? call, bool fullMediaViewVisible) =>
        !fullMediaViewVisible && Project(call, CallConnectionState.New) is not null;

    public static MobileDirectCallBarProjection? Project(CallSessionDto? call, CallConnectionState connectionState)
    {
        if (call is null || call.State is not (CallState.Ringing or CallState.Active)) return null;
        if (call.State == CallState.Ringing)
            return new(MobileDirectCallPhase.Calling, "Calling", false, true);
        return connectionState switch
        {
            CallConnectionState.Connected => new(MobileDirectCallPhase.Connected, "Connected", true, false),
            CallConnectionState.Disconnected => new(MobileDirectCallPhase.Reconnecting, "Reconnecting", false, false),
            CallConnectionState.Failed or CallConnectionState.Closed =>
                new(MobileDirectCallPhase.Failed, "Unable to connect.", false, false),
            _ => new(MobileDirectCallPhase.Connecting, "Connecting", false, false)
        };
    }
}
