namespace Iridium.Protocol;

public enum UserPresence
{
    Online,
    Idle,
    DoNotDisturb,
    Invisible
}

public enum PublicPresence
{
    Offline,
    Online,
    Idle,
    DoNotDisturb
}

public static class PresenceHubContract
{
    public const string SetPresence = "SetPresence";
    public const string PresenceChanged = "PresenceChanged";
}

public sealed record PresenceChangedEvent(Guid AccountId, PublicPresence Presence);
public sealed record UpdatePresenceRequest(UserPresence Presence);

public static class PresenceVisibility
{
    public static PublicPresence ToPublic(UserPresence preferred, bool connected = true) => !connected || preferred == UserPresence.Invisible
        ? PublicPresence.Offline
        : preferred switch
        {
            UserPresence.Idle => PublicPresence.Idle,
            UserPresence.DoNotDisturb => PublicPresence.DoNotDisturb,
            _ => PublicPresence.Online
        };
}
