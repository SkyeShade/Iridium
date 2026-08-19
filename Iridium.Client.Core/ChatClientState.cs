using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed class ChatClientState
{
    public ServerInfoDto? Server { get; internal set; }
    public bool IsConnected { get; internal set; }
}
