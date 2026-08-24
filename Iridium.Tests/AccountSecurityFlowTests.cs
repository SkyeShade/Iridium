using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Iridium.Client.Core;
using Iridium.Protocol;
using Iridium.Server.Api;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class AccountSecurityFlowTests
{
    [Fact(Timeout = 45_000)]
    public async Task LegacyPasswordsSupportLoginReauthenticationMigrationAndChangePassword()
    {
        var root = RepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"iridium-password-compatibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var databasePath = Path.Combine(temporaryDirectory, "compatibility.db");
        const string legacyPassword = "legacy-password";
        const string savedSessionToken = "saved-legacy-session-token";
        const string malformedSessionToken = "saved-malformed-session-token";
        var sessionAccountId = Guid.NewGuid();
        var loginAccountId = Guid.NewGuid();
        var legacyHash = LegacyPasswordHash(legacyPassword);
        await SeedCompatibilityAccountsAsync(databasePath, sessionAccountId, loginAccountId, legacyHash,
            savedSessionToken, malformedSessionToken);

        var address = $"http://127.0.0.1:{FreePort()}";
        using var server = StartServer(Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj"), address,
            databasePath);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();

        try
        {
            await WaitForServerAsync(address, server, output, error);

            var savedSession = new NodeClient(new Uri(address)) { AccessToken = savedSessionToken };
            var wrongPassword = await Assert.ThrowsAsync<NodeApiException>(() =>
                savedSession.UpdateRecoveryEmailAsync(new("wrong-password", "legacy@example.com")));
            Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
            Assert.Equal(legacyHash, await StoredPasswordHashAsync(databasePath, sessionAccountId));

            var status = await savedSession.UpdateRecoveryEmailAsync(new(legacyPassword, "Legacy@EXAMPLE.COM"));
            Assert.True(status.HasRecoveryEmail);
            Assert.Equal("L***@example.com", status.MaskedRecoveryEmail);
            Assert.NotEqual(legacyHash, await StoredPasswordHashAsync(databasePath, sessionAccountId));

            var wrongLogin = await Assert.ThrowsAsync<NodeApiException>(() =>
                new NodeClient(new Uri(address)).LoginAsync(new("legacy-login", "wrong-password")));
            Assert.Equal(HttpStatusCode.Unauthorized, wrongLogin.StatusCode);
            Assert.Equal(legacyHash, await StoredPasswordHashAsync(databasePath, loginAccountId));

            var legacyLogin = await new NodeClient(new Uri(address))
                .LoginAsync(new("legacy-login", legacyPassword));
            Assert.Equal(loginAccountId, legacyLogin.Account.Id);
            Assert.NotEqual(legacyHash, await StoredPasswordHashAsync(databasePath, loginAccountId));

            await savedSession.ChangePasswordAsync(new(legacyPassword, "changed-password", "changed-password"));
            await AssertUnauthorizedAsync(() =>
                new NodeClient(new Uri(address)).LoginAsync(new("legacy-session", legacyPassword)));
            var changed = await new NodeClient(new Uri(address))
                .LoginAsync(new("legacy-session", "changed-password"));
            Assert.Equal(sessionAccountId, changed.Account.Id);

            var malformedSession = new NodeClient(new Uri(address)) { AccessToken = malformedSessionToken };
            var malformedReauthentication = await Assert.ThrowsAsync<NodeApiException>(() =>
                malformedSession.UpdateRecoveryEmailAsync(new("any-password", "malformed@example.com")));
            Assert.Equal(HttpStatusCode.BadRequest, malformedReauthentication.StatusCode);
            await AssertUnauthorizedAsync(() =>
                new NodeClient(new Uri(address)).LoginAsync(new("malformed-hash", "any-password")));

            var newlyRegistered = new NodeClient(new Uri(address));
            await newlyRegistered.RegisterAsync(new("new-format", "New Format", "registered-password"));
            var newlyAuthenticated = await new NodeClient(new Uri(address))
                .LoginAsync(new("new-format", "registered-password"));
            Assert.Equal("new-format", newlyAuthenticated.Account.Username);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await DeleteDirectoryAsync(temporaryDirectory);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task PasswordRecoveryEmailAndRecoveryTokenFlowIsSecure()
    {
        var root = RepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"iridium-security-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var databasePath = Path.Combine(temporaryDirectory, "security.db");
        var address = $"http://127.0.0.1:{FreePort()}";
        using var server = StartServer(Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj"), address,
            databasePath);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();

        try
        {
            await WaitForServerAsync(address, server, output, error);
            var current = new NodeClient(new Uri(address));
            var registered = await current.RegisterAsync(new("security-user", "Security User", "old-password"));
            var accountId = registered.Account.Id;
            var otherSession = new NodeClient(new Uri(address));
            await otherSession.LoginAsync(new("security-user", "old-password"));

            await Assert.ThrowsAsync<NodeApiException>(() =>
                current.ChangePasswordAsync(new("wrong-password", "new-password", "new-password")));
            await Assert.ThrowsAsync<NodeApiException>(() =>
                current.ChangePasswordAsync(new("old-password", "new-password", "different-password")));

            await current.ChangePasswordAsync(new("old-password", "new-password", "new-password"));
            Assert.NotNull(await current.GetCurrentAccountAsync());
            await AssertUnauthorizedAsync(() => otherSession.GetCurrentAccountAsync());
            await AssertUnauthorizedAsync(() =>
                new NodeClient(new Uri(address)).LoginAsync(new("security-user", "old-password")));
            var active = new NodeClient(new Uri(address));
            await active.LoginAsync(new("security-user", "new-password"));

            await Assert.ThrowsAsync<NodeApiException>(() =>
                active.UpdateRecoveryEmailAsync(new("wrong-password", "User@EXAMPLE.COM")));
            var status = await active.UpdateRecoveryEmailAsync(new("new-password", "User@EXAMPLE.COM"));
            Assert.True(status.HasRecoveryEmail);
            Assert.Equal("U***@example.com", status.MaskedRecoveryEmail);

            var publicJson = await RawCurrentAccountJsonAsync(address, active.AccessToken!);
            Assert.DoesNotContain("recoveryEmail", publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(typeof(NodeAccountDto).GetProperties(), property =>
                property.Name.Contains("Recovery", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(typeof(ResolvedProfileDto).GetProperties(), property =>
                property.Name.Contains("Recovery", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(typeof(FriendSearchResultDto).GetProperties(), property =>
                property.Name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Session", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Recovery", StringComparison.OrdinalIgnoreCase));
            await AssertRecoveryEmailAsync(databasePath, accountId, "User@example.com", "user@example.com");

            await Assert.ThrowsAsync<NodeApiException>(() =>
                active.UpdateRecoveryEmailAsync(new("wrong-password", "changed@example.com")));
            status = await active.UpdateRecoveryEmailAsync(new("new-password", "Changed@EXAMPLE.COM"));
            Assert.Equal("C***@example.com", status.MaskedRecoveryEmail);
            await Assert.ThrowsAsync<NodeApiException>(() =>
                active.UpdateRecoveryEmailAsync(new("wrong-password", null)));
            status = await active.UpdateRecoveryEmailAsync(new("new-password", null));
            Assert.False(status.HasRecoveryEmail);
            status = await active.UpdateRecoveryEmailAsync(new("new-password", "recovery@example.com"));
            Assert.True(status.HasRecoveryEmail);

            var recoveryClient = new NodeClient(new Uri(address));
            var existingResponse = await recoveryClient.RequestPasswordRecoveryAsync(new("security-user"));
            var missingResponse = await recoveryClient.RequestPasswordRecoveryAsync(new("missing-user"));
            Assert.Equal(existingResponse.Message, missingResponse.Message);

            var expiredToken = "expired-recovery-token";
            await AddRecoveryTokenAsync(databasePath, accountId, expiredToken, DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.False((await recoveryClient.ValidatePasswordRecoveryAsync(new(expiredToken))).IsValid);
            await Assert.ThrowsAsync<NodeApiException>(() => recoveryClient.CompletePasswordRecoveryAsync(
                new("security-user", expiredToken, "recovered-password", "recovered-password")));

            var validToken = PasswordRecoveryTokens.Create();
            await AddRecoveryTokenAsync(databasePath, accountId, validToken, DateTimeOffset.UtcNow.AddMinutes(30));
            Assert.True((await recoveryClient.ValidatePasswordRecoveryAsync(new(validToken))).IsValid);
            Assert.False((await recoveryClient.ValidatePasswordRecoveryAsync(new(string.Empty))).IsValid);
            Assert.False((await recoveryClient.ValidatePasswordRecoveryAsync(new("invalid-recovery-token"))).IsValid);
            var secondActive = new NodeClient(new Uri(address));
            await secondActive.LoginAsync(new("security-user", "new-password"));
            await recoveryClient.CompletePasswordRecoveryAsync(
                new("security-user", validToken, "recovered-password", "recovered-password"));
            Assert.False((await recoveryClient.ValidatePasswordRecoveryAsync(new(validToken))).IsValid);

            await Assert.ThrowsAsync<NodeApiException>(() => recoveryClient.CompletePasswordRecoveryAsync(
                new("security-user", validToken, "another-password", "another-password")));
            await AssertUnauthorizedAsync(() => active.GetCurrentAccountAsync());
            await AssertUnauthorizedAsync(() => secondActive.GetCurrentAccountAsync());
            await AssertUnauthorizedAsync(() =>
                new NodeClient(new Uri(address)).LoginAsync(new("security-user", "new-password")));
            var recovered = await new NodeClient(new Uri(address))
                .LoginAsync(new("security-user", "recovered-password"));
            Assert.Equal(accountId, recovered.Account.Id);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await DeleteDirectoryAsync(temporaryDirectory);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task IceConfigurationRequiresAuthenticationAndReturnsEphemeralTurnCredentials()
    {
        var root = RepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"iridium-ice-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var databasePath = Path.Combine(temporaryDirectory, "ice.db");
        var address = $"http://127.0.0.1:{FreePort()}";
        using var server = StartServer(Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj"), address,
            databasePath, configureTurn: true);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();

        try
        {
            await WaitForServerAsync(address, server, output, error);
            using var anonymous = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.GetAsync("api/webrtc/ice-configuration")).StatusCode);

            var client = new NodeClient(new Uri(address));
            await client.RegisterAsync(new("ice-user", "ICE User", "ice-password"));
            var configuration = await client.GetWebRtcIceConfigurationAsync();
            Assert.Equal(2, configuration.IceServers.Count);
            var turn = configuration.IceServers.Single(value => value.Urls.Any(url => url.StartsWith("turn:")));
            Assert.NotNull(turn.Username);
            Assert.NotNull(turn.Credential);
            var expectedExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            Assert.InRange(configuration.ExpiresAt!.Value.ToUnixTimeSeconds(), expectedExpiry - 5, expectedExpiry + 5);

            using var authenticated = new HttpClient { BaseAddress = new Uri(address) };
            authenticated.DefaultRequestHeaders.Authorization = new("Bearer", client.AccessToken);
            var json = await authenticated.GetStringAsync("api/webrtc/ice-configuration");
            Assert.DoesNotContain("integration-turn-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("sharedSecret", json, StringComparison.OrdinalIgnoreCase);

            var community = await client.CreateCommunityAsync(new("Invite Authority", null));
            var invite = await client.CreateCommunityInviteAsync(community.Id,
                new(DateTimeOffset.UtcNow.AddHours(24), null));
            Assert.StartsWith("https://canonical.example/invite/", invite.InviteUrl, StringComparison.Ordinal);
            Assert.NotNull(CommunityInviteLink.Find(invite.InviteUrl!));
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await DeleteDirectoryAsync(temporaryDirectory);
        }
    }

    private static Process StartServer(string project, string address, string databasePath, bool configureTurn = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(project)!
        };
        start.ArgumentList.Add(typeof(AccountEndpoints).Assembly.Location);
        start.Environment["ASPNETCORE_URLS"] = address;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={databasePath}";
        start.Environment["Email__Enabled"] = "false";
        if (configureTurn)
        {
            start.Environment["WebRtc__IceServers__0__Urls__0"] = "stun:turn.example.net:3478";
            start.Environment["WebRtc__Turn__Enabled"] = "true";
            start.Environment["WebRtc__Turn__Urls__0"] = "turn:turn.example.net:3478?transport=udp";
            start.Environment["WebRtc__Turn__SharedSecret"] = "integration-turn-secret";
            start.Environment["WebRtc__Turn__CredentialLifetimeSeconds"] = "3600";
            start.Environment["Node__PublicAuthority"] = "https://canonical.example";
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the test Node.");
    }

    private static async Task WaitForServerAsync(string address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"Test Node stopped early.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("api/server-info")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Test Node did not become ready.");
    }

    private static async Task AssertUnauthorizedAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<NodeApiException>(action);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    private static async Task<string> RawCurrentAccountJsonAsync(string address, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return await http.GetStringAsync("api/account/current");
    }

    private static DbContextOptions<IridiumDbContext> DatabaseOptions(string databasePath) =>
        new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite($"Data Source={databasePath}").Options;

    private static async Task SeedCompatibilityAccountsAsync(
        string databasePath,
        Guid sessionAccountId,
        Guid loginAccountId,
        string legacyHash,
        string savedSessionToken,
        string malformedSessionToken)
    {
        await using var db = new IridiumDbContext(DatabaseOptions(databasePath));
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var sessionAccount = new NodeAccount
        {
            Id = sessionAccountId,
            Username = "legacy-session",
            DisplayName = "Legacy Session",
            PasswordHash = legacyHash,
            CreatedAt = now
        };
        var loginAccount = new NodeAccount
        {
            Id = loginAccountId,
            Username = "legacy-login",
            DisplayName = "Legacy Login",
            PasswordHash = legacyHash,
            CreatedAt = now
        };
        var malformedAccount = new NodeAccount
        {
            Id = Guid.NewGuid(),
            Username = "malformed-hash",
            DisplayName = "Malformed Hash",
            PasswordHash = "not-a-supported-password-hash",
            CreatedAt = now
        };
        db.Accounts.AddRange(sessionAccount, loginAccount, malformedAccount);
        db.AccountSessions.AddRange(
            StoredSession(sessionAccount, savedSessionToken, now),
            StoredSession(malformedAccount, malformedSessionToken, now));
        await db.SaveChangesAsync();
    }

    private static AccountSession StoredSession(NodeAccount account, string token, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        Account = account,
        TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
        CreatedAt = now,
        LastUsedAt = now,
        ExpiresAt = now.AddDays(1)
    };

    private static string LegacyPasswordHash(string password)
    {
        var salt = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA512, 32);
        return $"v1.210000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static async Task<string> StoredPasswordHashAsync(string databasePath, Guid accountId)
    {
        await using var db = new IridiumDbContext(DatabaseOptions(databasePath));
        return await db.Accounts.Where(value => value.Id == accountId).Select(value => value.PasswordHash).SingleAsync();
    }

    private static async Task AssertRecoveryEmailAsync(
        string databasePath, Guid accountId, string display, string normalized)
    {
        await using var db = new IridiumDbContext(DatabaseOptions(databasePath));
        var account = await db.Accounts.AsNoTracking().SingleAsync(value => value.Id == accountId);
        Assert.Equal(display, account.RecoveryEmail);
        Assert.Equal(normalized, account.RecoveryEmailNormalized);
    }

    private static async Task AddRecoveryTokenAsync(
        string databasePath, Guid accountId, string token, DateTimeOffset expiresAt)
    {
        await using var db = new IridiumDbContext(DatabaseOptions(databasePath));
        var account = await db.Accounts.SingleAsync(value => value.Id == accountId);
        db.PasswordRecoveryTokens.Add(new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Account = account,
            TokenHash = PasswordRecoveryTokens.Hash(token),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync();
        var stored = await db.PasswordRecoveryTokens.AsNoTracking()
            .SingleAsync(value => value.TokenHash == PasswordRecoveryTokens.Hash(token));
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Iridium repository root.");
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException) { if (attempt == 19) return; await Task.Delay(100); }
            catch (UnauthorizedAccessException) { if (attempt == 19) return; await Task.Delay(100); }
        }
    }
}
