using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public enum RealtimeLifecycleState { Disconnected, Connecting, Connected, Reconnecting, Recovering }

public sealed record RealtimeLifecycleSnapshot(RealtimeLifecycleState State, HubConnection? Connection,
    int Generation, string Reason, string? ConnectionId);

public sealed record RealtimeRecoveryContext(HubConnection Connection, int Generation, string Reason,
    string? ConnectionId);

public sealed class RealtimeConnectionService(NodeSession session, ILogger<RealtimeConnectionService> logger)
    : IAsyncDisposable
{
    private static int _nextInstanceId;
    private readonly int _instanceId = Interlocked.Increment(ref _nextInstanceId);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _recoverySync = new();
    private readonly Dictionary<Guid, (string Name, Func<RealtimeRecoveryContext, CancellationToken, Task> Handler)>
        _recoveryHandlers = [];
    private HubConnection? _connection;
    private Uri? _node;
    private Guid? _accountId;
    private int _connectionGeneration;
    private int _deferredRecoveryRequested;
    private RealtimeLifecycleState _lifecycleState = RealtimeLifecycleState.Disconnected;
    private bool _disposed;

    public HubConnection? Connection => _connection;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public RealtimeLifecycleState LifecycleState => _lifecycleState;
    public event Action<RealtimeLifecycleSnapshot>? LifecycleChanged;

    public IDisposable RegisterRecoveryHandler(string name,
        Func<RealtimeRecoveryContext, CancellationToken, Task> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        var id = Guid.NewGuid();
        lock (_recoverySync) _recoveryHandlers[id] = (name, handler);
        return new RecoveryRegistration(this, id);
    }

    public async Task<HubConnection> EnsureConnectedAsync(string reason, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await ConnectAsync(reason, recoverConnected: false, allowUnauthenticated: false, cancellationToken);
        return result.Connection ?? throw new InvalidOperationException("The realtime connection could not be established.");
    }

    public async Task VerifyAndRecoverAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (_disposed || session.Account is null) return;
        var result = await ConnectAsync(reason, recoverConnected: true, allowUnauthenticated: true, cancellationToken);
        if (result.Recovery is not null) await RunRecoveryPipelineAsync(result.Recovery, cancellationToken);
    }

    private async Task<(HubConnection? Connection, RealtimeRecoveryContext? Recovery)> ConnectAsync(
        string reason, bool recoverConnected, bool allowUnauthenticated, CancellationToken cancellationToken)
    {
        RealtimeRecoveryContext? recovery = null;
        HubConnection? result = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return (null, null);
            if (session.Account is null)
            {
                if (allowUnauthenticated) return (null, null);
                throw new InvalidOperationException("Log in before connecting realtime services.");
            }

            var client = session.AuthorizedClient;
            var accountId = session.Account.Id;
            if (_connection is not null && _node == client.NodeAddress && _accountId == accountId)
            {
                result = _connection;
                if (result.State == HubConnectionState.Connected)
                {
                    if (recoverConnected) Interlocked.Exchange(ref _deferredRecoveryRequested, 0);
                    Diagnostic(recoverConnected ? "VERIFY" : "REUSE", reason, result, _connectionGeneration);
                    if (recoverConnected)
                    {
                        SetLifecycleState(RealtimeLifecycleState.Recovering, reason, result, _connectionGeneration);
                        recovery = new(result, _connectionGeneration, reason, result.ConnectionId);
                    }
                    return (result, recovery);
                }

                if (result.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting)
                {
                    Diagnostic("VERIFY-DEFERRED", $"{reason}; state={result.State}", result, _connectionGeneration);
                    if (recoverConnected)
                    {
                        Interlocked.Exchange(ref _deferredRecoveryRequested, 1);
                        return (result, null);
                    }
                    throw new InvalidOperationException(
                        $"The realtime connection is currently {result.State.ToString().ToLowerInvariant()}.");
                }

                var generation = _connectionGeneration;
                SetLifecycleState(RealtimeLifecycleState.Connecting, reason, result, generation);
                Diagnostic("START", reason, result, generation);
                await result.StartAsync(cancellationToken);
                if (!IsCurrent(result, generation)) return (_connection, null);
                Diagnostic("STARTED", reason, result, generation);
                SetLifecycleState(RealtimeLifecycleState.Recovering, reason, result, generation);
                recovery = new(result, generation, $"fresh-start:{reason}", result.ConnectionId);
                return (result, recovery);
            }

            if (_connection is not null) await DisposeConnectionAsync("account or node changed");
            _node = client.NodeAddress;
            _accountId = accountId;
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(client.NodeAddress, "hubs/chat"), options =>
                    options.AccessTokenProvider = () => Task.FromResult(client.AccessToken))
                .WithAutomaticReconnect()
                .Build();
            var generationForConnection = ++_connectionGeneration;
            _connection = connection;
            result = connection;
            RegisterLifecycleHandlers(connection, generationForConnection);
            Diagnostic("CREATE", reason, connection, generationForConnection);
            SetLifecycleState(RealtimeLifecycleState.Connecting, reason, connection, generationForConnection);
            Diagnostic("START", reason, connection, generationForConnection);
            await connection.StartAsync(cancellationToken);
            if (!IsCurrent(connection, generationForConnection)) return (_connection, null);
            Diagnostic("STARTED", reason, connection, generationForConnection);
            SetLifecycleState(RealtimeLifecycleState.Recovering, reason, connection, generationForConnection);
            recovery = new(connection, generationForConnection, $"initial-start:{reason}", connection.ConnectionId);
            return (result, recovery);
        }
        catch
        {
            if (result is not null && IsCurrent(result, _connectionGeneration))
                SetLifecycleState(RealtimeLifecycleState.Disconnected, $"start-failed:{reason}", result,
                    _connectionGeneration);
            throw;
        }
        finally
        {
            _gate.Release();
            if (recovery is not null && !recoverConnected)
                _ = RunRecoveryPipelineSafelyAsync(recovery);
        }
    }

    private async Task RunRecoveryPipelineSafelyAsync(RealtimeRecoveryContext context)
    {
        try { await RunRecoveryPipelineAsync(context, CancellationToken.None); }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Realtime recovery pipeline failed for generation {Generation} ({Reason}).",
                context.Generation, context.Reason);
        }
    }

    private void RegisterLifecycleHandlers(HubConnection connection, int generation)
    {
        connection.Reconnecting += exception =>
        {
            if (!IsCurrent(connection, generation)) return Task.CompletedTask;
            var reason = exception?.Message ?? "transport reconnect";
            Diagnostic("RECONNECTING", reason, connection, generation);
            SetLifecycleState(RealtimeLifecycleState.Reconnecting, reason, connection, generation);
            return Task.CompletedTask;
        };
        connection.Reconnected += connectionId => HandleReconnectedAsync(connection, generation, connectionId);
        connection.Closed += exception =>
        {
            if (!IsCurrent(connection, generation)) return Task.CompletedTask;
            var reason = exception?.Message ?? "clean close";
            Diagnostic("CLOSED", reason, connection, generation);
            SetLifecycleState(RealtimeLifecycleState.Disconnected, reason, connection, generation);
            if (Interlocked.Exchange(ref _deferredRecoveryRequested, 0) == 1)
                _ = VerifyAfterDeferredCloseAsync(generation);
            return Task.CompletedTask;
        };
    }

    private async Task HandleReconnectedAsync(HubConnection connection, int generation, string? connectionId)
    {
        if (!IsCurrent(connection, generation)) return;
        Interlocked.Exchange(ref _deferredRecoveryRequested, 0);
        Diagnostic("RECONNECTED", $"newConnectionId={Short(connectionId)}", connection, generation);
        SetLifecycleState(RealtimeLifecycleState.Recovering, "automatic-reconnected", connection, generation);
        await RunRecoveryPipelineAsync(new(connection, generation, "automatic-reconnected", connectionId),
            CancellationToken.None);
    }

    private async Task VerifyAfterDeferredCloseAsync(int generation)
    {
        await Task.Yield();
        if (_disposed || generation != _connectionGeneration) return;
        try { await VerifyAndRecoverAsync("deferred-resume-after-closed"); }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Deferred realtime recovery failed after generation {Generation} closed.", generation);
        }
    }

    private async Task RunRecoveryPipelineAsync(RealtimeRecoveryContext context, CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrent(context.Connection, context.Generation) ||
                context.Connection.State != HubConnectionState.Connected) return;
            Diagnostic("RECOVERY-START", context.Reason, context.Connection, context.Generation);
            (string Name, Func<RealtimeRecoveryContext, CancellationToken, Task> Handler)[] handlers;
            lock (_recoverySync) handlers = _recoveryHandlers.Values.ToArray();
            await Task.WhenAll(handlers.Select(value => RunRecoveryHandlerAsync(value.Name, value.Handler,
                context, cancellationToken)));

            if (IsCurrent(context.Connection, context.Generation) &&
                context.Connection.State == HubConnectionState.Connected)
            {
                SetLifecycleState(RealtimeLifecycleState.Connected, context.Reason, context.Connection,
                    context.Generation);
                Diagnostic("RECOVERY-COMPLETE", context.Reason, context.Connection, context.Generation);
            }
        }
        finally { _recoveryGate.Release(); }
    }

    private async Task RunRecoveryHandlerAsync(string name,
        Func<RealtimeRecoveryContext, CancellationToken, Task> handler,
        RealtimeRecoveryContext context, CancellationToken cancellationToken)
    {
        if (!IsCurrent(context.Connection, context.Generation)) return;
        try
        {
            await handler(context, cancellationToken);
            logger.LogDebug("Realtime recovery handler {Handler} completed for generation {Generation} ({Reason}).",
                name, context.Generation, context.Reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Realtime recovery handler {Handler} failed for generation {Generation} ({Reason}); a later resume can retry it.",
                name, context.Generation, context.Reason);
        }
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
        var generation = _connectionGeneration;
        _connection = null;
        _connectionGeneration++;
        if (connection is not null)
        {
            Diagnostic("DISPOSE", reason, connection, generation);
            await connection.DisposeAsync();
        }
        _node = null;
        _accountId = null;
        Interlocked.Exchange(ref _deferredRecoveryRequested, 0);
        SetLifecycleState(RealtimeLifecycleState.Disconnected, reason, null, _connectionGeneration);
    }

    private bool IsCurrent(HubConnection connection, int generation) =>
        !_disposed && ReferenceEquals(_connection, connection) && _connectionGeneration == generation;

    private void SetLifecycleState(RealtimeLifecycleState state, string reason, HubConnection? connection,
        int generation)
    {
        if (connection is not null && !IsCurrent(connection, generation)) return;
        _lifecycleState = state;
        var snapshot = new RealtimeLifecycleSnapshot(state, connection, generation, reason, connection?.ConnectionId);
        foreach (var callback in LifecycleChanged?.GetInvocationList().Cast<Action<RealtimeLifecycleSnapshot>>() ?? [])
        {
            try { callback(snapshot); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A realtime lifecycle observer failed while handling {State}.", state);
            }
        }
    }

    private void Diagnostic(string operation, string reason, HubConnection connection, int generation) => logger.LogDebug(
        "REALTIME {Operation}: AccountId={AccountId} ServiceInstance={ServiceInstance} " +
        "ConnectionGeneration={ConnectionGeneration} ConnectionId={ConnectionId} State={State} Reason={Reason}",
        operation, _accountId, _instanceId, generation, Short(connection.ConnectionId), connection.State, reason);

    private void RemoveRecoveryHandler(Guid id)
    {
        lock (_recoverySync) _recoveryHandlers.Remove(id);
    }

    private static string? Short(string? value) => value is null || value.Length <= 8 ? value : value[..8];

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync("realtime service disposed");
        _disposed = true;
        lock (_recoverySync) _recoveryHandlers.Clear();
        _recoveryGate.Dispose();
        _gate.Dispose();
    }

    private sealed class RecoveryRegistration(RealtimeConnectionService owner, Guid id) : IDisposable
    {
        private RealtimeConnectionService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveRecoveryHandler(id);
    }
}
