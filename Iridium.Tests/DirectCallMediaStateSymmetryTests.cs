namespace Iridium.Tests;

public sealed class DirectCallMediaStateSymmetryTests
{
    [Theory]
    [InlineData("caller")]
    [InlineData("callee")]
    public async Task LiveKitConnectedCallbackTransitionsEitherLocalRoleAndRaisesChanged(string role)
    {
        var service = CreateCallClient();
        SetField(service, "_mediaRole", role);
        SetField(service, "_activeMediaMode", Iridium.Protocol.MediaMode.NodeSfu);
        var changed = 0;
        service.Changed += () => changed++;

        var callback = typeof(Iridium.Client.Core.CallClientService).GetMethod(
            "MediaConnectionChangedAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)callback.Invoke(service, [Iridium.Protocol.CallConnectionState.Connected])!;

        Assert.Equal(Iridium.Protocol.CallConnectionState.Connected, service.MediaConnectionState);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void CallerBeginsConnectingBeforeLiveKitInitializationSoConnectedCallbackWins()
    {
        var method = Method(CallClientSource(),
            "private async Task ReceiveAcceptedAsync", "private async Task ReceiveTerminalAsync");

        Assert.True(method.IndexOf("MediaConnectionState = CallConnectionState.Connecting", StringComparison.Ordinal) <
                    method.IndexOf("await StartMediaAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void CalleeAcceptBeginsConnectingBeforeLiveKitInitializationSoConnectedCallbackWins()
    {
        var method = Method(CallClientSource(), "public async Task AcceptAsync", "public async Task DeclineAsync");
        var startMedia = method.IndexOf("await StartMediaAsync(cancellationToken)", StringComparison.Ordinal);

        Assert.True(method.IndexOf("MediaConnectionState = CallConnectionState.Connecting", StringComparison.Ordinal) < startMedia);
        Assert.DoesNotContain("MediaConnectionState = CallConnectionState.Connecting", method[(startMedia + 1)..]);
        Assert.Contains("NotifyChanged();\n                await StartMediaAsync(cancellationToken);", Normalize(method));
    }

    [Fact]
    public void LocalLiveKitStateIsAuthoritativeAndNotifiesUiForEitherRole()
    {
        var root = FindRepositoryRoot();
        var callClient = CallClientSource();
        var mediaService = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Services", "LiveKitCallMediaService.cs"));
        var liveKit = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "liveKitMedia.js"));
        var stage = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectVoiceCallStage.razor"));
        var callback = Method(callClient,
            "private async Task MediaConnectionChangedAsync", "private Task MediaIceConnectionChangedAsync");

        Assert.Contains("media.ConnectionStateChanged += MediaConnectionChangedAsync", callClient);
        Assert.Contains("generation == _context?.PeerGeneration ? InvokeHandlers(ConnectionStateChanged", mediaService);
        Assert.Contains("RoomEvent.ConnectionStateChanged", liveKit);
        Assert.Contains("RoomEvent.Reconnecting", liveKit);
        Assert.Contains("RoomEvent.Reconnected", liveKit);
        Assert.Contains("RoomEvent.Reconnecting, () => reportState(session, \"disconnected\")", liveKit);
        Assert.Contains("await reportState(session, \"connected\")", liveKit);
        Assert.Contains("MediaConnectionState = state", callback);
        Assert.Contains("NotifyChanged()", callback);
        Assert.Contains("LiveKitRoomConnected", callClient);
        Assert.Contains("Calls.MediaConnectionState == CallConnectionState.Connected && ScreenShareCapability.IsSupported", stage);
        var shareGate = stage[stage.IndexOf("private bool CanStartScreenShare", StringComparison.Ordinal)..];
        Assert.DoesNotContain("CallerAccountId", shareGate);
    }

    [Fact]
    public void SfuRetryTransitionsToConnectingBeforeLiveKitCanReportConnected()
    {
        var method = Method(CallClientSource(), "public async Task RetryMediaAsync", "public void DismissStatus");
        var sfu = method[method.IndexOf("if (_activeMediaMode == MediaMode.NodeSfu)", StringComparison.Ordinal)..];

        Assert.True(sfu.IndexOf("MediaConnectionState = CallConnectionState.Connecting", StringComparison.Ordinal) <
                    sfu.IndexOf("await StartMediaAsync(cancellationToken)", StringComparison.Ordinal));
    }

    private static string CallClientSource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "Iridium.Client.Core", "CallClientService.cs"));

    private static string Method(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static Iridium.Client.Core.CallClientService CreateCallClient()
    {
        var nodeSession = new Iridium.Client.Core.NodeSession(
            new EmptyAccountStore(), new EmptySelectionStore(), new EmptyTokenStore());
        var realtime = new Iridium.Client.Core.RealtimeConnectionService(nodeSession,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iridium.Client.Core.RealtimeConnectionService>.Instance);
        var media = System.Reflection.DispatchProxy.Create<Iridium.Client.Core.ICallMediaService, MediaProxy>();
        return new(nodeSession, realtime, media,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iridium.Client.Core.CallClientService>.Instance);
    }

    private static void SetField<T>(Iridium.Client.Core.CallClientService service, string name, T value) =>
        typeof(Iridium.Client.Core.CallClientService).GetField(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, value);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    public class MediaProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_DiagnosticsEnabled" ? true : null;
    }

    private sealed class EmptyAccountStore : Iridium.Client.Core.ISavedAccountStore
    {
        public Task<Iridium.Client.Core.SavedAccountStoreData> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Iridium.Client.Core.SavedAccountStoreData.Empty);
        public Task SaveAsync(Iridium.Client.Core.SavedAccountStoreData data, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptySelectionStore : Iridium.Client.Core.IActiveAccountSelectionStore
    {
        public Task<Iridium.Client.Core.SavedAccountKey?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Iridium.Client.Core.SavedAccountKey?>(null);
        public Task SaveAsync(Iridium.Client.Core.SavedAccountKey? key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyTokenStore : Iridium.Client.Core.INodeTokenStore
    {
        public Task<string?> LoadAsync(string nodeAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task SaveAsync(string nodeAddress, string token, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveAsync(string nodeAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
