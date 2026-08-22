using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iridium.Tests;

public sealed class AccountSwitchingTests
{
    [Fact(Timeout = 30_000)]
    public async Task SameNodeAccountsSwitchRepeatedlyAndLogoutOnlyClearsTheActiveCredential()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"iridium-switch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var node = new SavedNode($"http://127.0.0.1:{FreePort()}", "Test Node", true);
        using var server = StartServer(Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj"), node.Address,
            Path.Combine(temporaryDirectory, "switching.db"));
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();

        try
        {
            await WaitForServerAsync(node.Address, server, output, error);
            var store = new MemoryAccountStore();
            var tabASelection = new MemorySelectionStore();
            var session = new NodeSession(store, tabASelection, new EmptyLegacyTokenStore());
            var communities = new CommunitySession(session);
            await using var realtime = new RealtimeConnectionService(session, NullLogger<RealtimeConnectionService>.Instance);
            await using var messaging = new ChannelMessagingSession(session, realtime,
                NullLogger<ChannelMessagingSession>.Instance);
            var switching = new AccountSwitchService(session, communities, messaging);

            await switching.InitializeAsync([node]);
            switching.BeginAuthentication(node);
            await switching.RegisterAsync("alpha", "Alpha", "test-password");
            var alpha = session.ActiveSavedAccount!;
            await session.CreateCommunityAsync("Alpha Community", null);

            switching.BeginAuthentication(node);
            await switching.RegisterAsync("beta", "Beta", "test-password");
            var beta = session.ActiveSavedAccount!;

            Assert.NotEqual(alpha.AccountId, beta.AccountId);
            Assert.Equal(2, session.SavedAccounts.Count);
            Assert.Empty(session.Communities);

            var tabBSelection = new MemorySelectionStore(beta.Key);
            var tabBSession = new NodeSession(store, tabBSelection, new EmptyLegacyTokenStore());
            var tabBCommunities = new CommunitySession(tabBSession);
            await using var tabBRealtime = new RealtimeConnectionService(tabBSession,
                NullLogger<RealtimeConnectionService>.Instance);
            await using var tabBMessaging = new ChannelMessagingSession(tabBSession, tabBRealtime,
                NullLogger<ChannelMessagingSession>.Instance);
            var tabBSwitching = new AccountSwitchService(tabBSession, tabBCommunities, tabBMessaging);
            await tabBSwitching.InitializeAsync([node]);
            Assert.Equal(beta.AccountId, tabBSession.Account!.Id);

            foreach (var key in new[] { alpha.Key, beta.Key, alpha.Key, beta.Key, alpha.Key })
                Assert.True(await switching.SwitchAsync(key));

            Assert.Equal(beta.AccountId, tabBSession.Account!.Id);
            Assert.Equal(beta.Key, await tabBSelection.LoadAsync());

            Assert.Equal(alpha.AccountId, session.Account!.Id);
            Assert.Equal("Alpha Community", Assert.Single(session.Communities).Name);
            Assert.All(store.Data.Accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.SessionToken)));

            var alphaCommunity = Assert.Single(session.Communities);
            await communities.LoadAsync(alphaCommunity.Id);
            var general = Assert.Single(communities.Channels);
            await messaging.OpenChannelAsync(alphaCommunity.Id, general.Id);
            await messaging.SendAsync("before account switch");
            Assert.Single(messaging.Messages);

            Assert.True(await switching.SwitchAsync(beta.Key));
            Assert.True(await switching.SwitchAsync(alpha.Key));
            await communities.LoadAsync(alphaCommunity.Id);
            await messaging.OpenChannelAsync(alphaCommunity.Id, general.Id);
            await messaging.SendAsync("after account switch");
            Assert.Equal(2, messaging.Messages.Count);

            await switching.LogoutAsync();
            var loggedOutAlpha = store.Data.Accounts.Single(account => account.AccountId == alpha.AccountId);
            var stillReadyBeta = store.Data.Accounts.Single(account => account.AccountId == beta.AccountId);
            Assert.Null(loggedOutAlpha.SessionToken);
            Assert.Equal(SavedAccountStatus.LoginRequired, loggedOutAlpha.Status);
            Assert.False(string.IsNullOrWhiteSpace(stillReadyBeta.SessionToken));
            Assert.True(await switching.SwitchAsync(beta.Key));
            Assert.Equal(beta.AccountId, session.Account!.Id);

            var directConversation = await session.OpenDirectConversationAsync(alpha.AccountId);
            await messaging.OpenDirectConversationAsync(directConversation.Id);
            for (var index = 0; index < 10; index++)
                await messaging.SendDirectAsync($"deduplication check {index}");
            Assert.Single(session.DirectConversations);
            Assert.Equal(directConversation.Id, session.DirectConversations[0].Id);

            store.Replace(store.Data with
            {
                Accounts = store.Data.Accounts.Select(account => account.AccountId == beta.AccountId
                    ? account with { SessionToken = "invalid-token", Status = SavedAccountStatus.Ready }
                    : account).ToArray(),
                ActiveNodeAddress = null,
                ActiveAccountId = null
            });
            var expiredSelection = new MemorySelectionStore(beta.Key);
            var restored = new NodeSession(store, expiredSelection, new EmptyLegacyTokenStore());
            await restored.InitializeAsync([node]);
            Assert.False(restored.IsAuthenticated);
            Assert.Equal(2, restored.SavedAccounts.Count);
            Assert.Equal(SavedAccountStatus.LoginRequired,
                restored.SavedAccounts.Single(account => account.AccountId == beta.AccountId).Status);
            Assert.Equal(beta.Key, restored.ReauthenticationAccount!.Key);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await DeleteDirectoryAsync(temporaryDirectory);
        }
    }

    private static Process StartServer(string project, string address, string databasePath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration", "Release", "--no-launch-profile" })
            start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={databasePath}";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the test Node.");
    }

    private static async Task WaitForServerAsync(string address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (server.HasExited) throw new InvalidOperationException($"Test Node stopped early.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("api/server-info")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Test Node did not become ready.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100);
            }
        }
    }

    private sealed class MemoryAccountStore : ISavedAccountStore
    {
        public SavedAccountStoreData Data { get; private set; } = SavedAccountStoreData.Empty;
        public void Replace(SavedAccountStoreData data) => Data = data;
        public Task<SavedAccountStoreData> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Data);
        public Task SaveAsync(SavedAccountStoreData data, CancellationToken cancellationToken = default)
        {
            Data = data;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySelectionStore(SavedAccountKey? initial = null) : IActiveAccountSelectionStore
    {
        private SavedAccountKey? _key = initial;
        public Task<SavedAccountKey?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_key);
        public Task SaveAsync(SavedAccountKey? key, CancellationToken cancellationToken = default)
        {
            _key = key;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyLegacyTokenStore : INodeTokenStore
    {
        public Task<string?> LoadAsync(string nodeAddress, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string nodeAddress, string token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string nodeAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
