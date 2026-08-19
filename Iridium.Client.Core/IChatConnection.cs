using Iridium.Protocol;

namespace Iridium.Client.Core;

public interface IChatConnection
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(SendChatMessage message, CancellationToken cancellationToken = default);
}
