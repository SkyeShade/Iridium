using System.Security.Cryptography;
using System.Text;
using Iridium.Server.Calls;
using Iridium.Server.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Iridium.Tests;

public sealed class WebRtcIceConfigurationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-24T00:00:00Z");

    [Fact]
    public void TurnDisabledReturnsConfiguredStunOnly()
    {
        var service = Create(new WebRtcOptions
        {
            IceServers = [new WebRtcIceServerOptions { Urls = ["stun:stun.example.net:3478"] }],
            Turn = new WebRtcTurnOptions
            {
                Enabled = false, Urls = ["turn:turn.example.net:3478"], SharedSecret = "must-not-leak"
            }
        });

        var result = service.Create(Guid.NewGuid());

        var server = Assert.Single(result.IceServers);
        Assert.Equal("stun:stun.example.net:3478", Assert.Single(server.Urls));
        Assert.Null(server.Username);
        Assert.Null(server.Credential);
        Assert.Null(result.ExpiresAt);
        Assert.Equal("all", result.IceTransportPolicy);
    }

    [Fact]
    public void TurnEnabledUsesCoturnRestHmacSha1AndConfiguredExpiry()
    {
        const string secret = "server-side-shared-secret";
        var accountId = Guid.Parse("af2a83af-8d36-4bef-bd4f-06012492879e");
        var service = Create(new WebRtcOptions
        {
            IceServers = [new WebRtcIceServerOptions { Urls = ["stun:turn.example.net:3478"] }],
            Turn = new WebRtcTurnOptions
            {
                Enabled = true,
                Urls = ["turn:turn.example.net:3478?transport=udp", "turns:turn.example.net:5349?transport=tcp"],
                SharedSecret = secret,
                CredentialLifetimeSeconds = 3600
            }
        });

        var result = service.Create(accountId);

        Assert.Equal(Now.AddHours(1), result.ExpiresAt);
        Assert.Equal(2, result.IceServers.Count);
        var turn = result.IceServers[1];
        Assert.Equal($"{result.ExpiresAt!.Value.ToUnixTimeSeconds()}:{accountId:N}", turn.Username);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(turn.Username!)));
        Assert.Equal(expected, turn.Credential);
        Assert.DoesNotContain(result.IceServers, value => value.Username == secret || value.Credential == secret);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.True(result.ExpiresAt > Now);
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(3600, 3600)]
    [InlineData(999999, 86400)]
    public void CredentialLifetimeIsBounded(int configured, int expected)
    {
        var service = Create(new WebRtcOptions
        {
            Turn = new WebRtcTurnOptions
            {
                Enabled = true, Urls = ["turn:turn.example.net:3478"], SharedSecret = "secret",
                CredentialLifetimeSeconds = configured
            }
        });

        Assert.Equal(Now.AddSeconds(expected), service.Create(Guid.NewGuid()).ExpiresAt);
    }

    [Fact]
    public void MisconfiguredTurnFailsClosedWithoutReturningCredentials()
    {
        var service = Create(new WebRtcOptions
        {
            Turn = new WebRtcTurnOptions { Enabled = true, Urls = ["turn:turn.example.net:3478"] }
        });

        var result = service.Create(Guid.NewGuid());

        Assert.Empty(result.IceServers);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public void MisconfiguredTurnProducesClearServerSideErrorWithoutSecrets()
    {
        const string secret = "must-not-appear-in-the-log";
        var logger = new CapturingLogger<WebRtcIceConfigurationService>();
        var service = new WebRtcIceConfigurationService(Options.Create(new WebRtcOptions
        {
            Turn = new WebRtcTurnOptions { Enabled = true, SharedSecret = secret }
        }), new TestTimeProvider(Now), logger);

        service.Create(Guid.NewGuid());

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error).Message;
        Assert.Contains("TURN is enabled but unusable", error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("all", "all")]
    [InlineData("All", "all")]
    [InlineData("relay", "relay")]
    [InlineData("Relay", "relay")]
    [InlineData("invalid", "all")]
    public void IceTransportPolicyIsRestrictedToAllOrRelay(string configured, string expected)
    {
        var service = Create(new WebRtcOptions { IceTransportPolicy = configured });

        Assert.Equal(expected, service.Create(Guid.NewGuid()).IceTransportPolicy);
    }

    private static WebRtcIceConfigurationService Create(WebRtcOptions options) =>
        new(Options.Create(options), new TestTimeProvider(Now), NullLogger<WebRtcIceConfigurationService>.Instance);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
