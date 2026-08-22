using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class RealtimeConnectionService(NodeSession session, ILogger<RealtimeConnectionService> logger)
    : IAsyncDisposable
{
    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    private static int _nextInstanceId;
    private readonly int _instanceId = Interlocked.Increment(ref _nextInstanceId);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private Uri? _node;
    private Guid? _accountId;
    private int _connectionGeneration;
    private bool _disposed;

    public HubConnection? Connection => _connection;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task<HubConnection> EnsureConnectedAsync(string reason, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = session.AuthorizedClient;
            var accountId = session.Account?.Id
                ?? throw new InvalidOperationException("Log in before connecting realtime services.");
            if (_connection is not null && _node == client.NodeAddress && _accountId == accountId)
            {
                if (_connection.State == HubConnectionState.Connected)
                {
                    Diagnostic("REUSE", reason, _connection);
                    return _connection;
                }
                if (_connection.State != HubConnectionState.Disconnected)
                    throw new InvalidOperationException($"The realtime connection is currently {_connection.State.ToString().ToLowerInvariant()}.");
                Diagnostic("START", reason, _connection);
                await _connection.StartAsync(cancellationToken);
                Diagnostic("STARTED", reason, _connection);
                return _connection;
            }

            if (_connection is not null) await DisposeConnectionAsync("account or node changed");
            _node = client.NodeAddress;
            _accountId = accountId;
            _connectionGeneration++;
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(client.NodeAddress, "hubs/chat"), options =>
                    options.AccessTokenProvider = () => Task.FromResult(client.AccessToken))
                .WithAutomaticReconnect()
                .Build();
            _connection = connection;
            Diagnostic("CREATE", reason, connection);
            connection.Reconnecting += exception =>
            {
                Diagnostic("RECONNECTING", exception?.Message ?? "transport reconnect", connection);
                return Task.CompletedTask;
            };
            connection.Reconnected += connectionId =>
            {
                Diagnostic("RECONNECTED", $"newConnectionId={Short(connectionId)}", connection);
                return Task.CompletedTask;
            };
            connection.Closed += exception =>
            {
                Diagnostic("CLOSED", exception?.Message ?? "clean close", connection);
                return Task.CompletedTask;
            };
            Diagnostic("START", reason, connection);
            await connection.StartAsync(cancellationToken);
            Diagnostic("STARTED", reason, connection);
            return connection;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await DisposeConnectionAsync(reason); }
        finally { _gate.Release(); }
    }

    private async Task DisposeConnectionAsync(string reason)
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            Diagnostic("DISPOSE", reason, connection);
            await connection.DisposeAsync();
        }
        _node = null;
        _accountId = null;
    }

    private void Diagnostic(string operation, string reason, HubConnection connection) => logger.LogDebug(
        "REALTIME DIAGNOSTIC {Operation}: AccountId={AccountId} ServiceInstance={ServiceInstance} " +
        "ConnectionGeneration={ConnectionGeneration} ConnectionId={ConnectionId} Reason={Reason}",
        operation, _accountId, _instanceId, _connectionGeneration, Short(connection.ConnectionId), reason);

    private static string? Short(string? value) => value is null || value.Length <= 8 ? value : value[..8];

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync("realtime service disposed");
        _gate.Dispose();
    }
}
