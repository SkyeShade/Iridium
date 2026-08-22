using Iridium.Protocol;
using Iridium.Server.Hubs;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Iridium.Tests;

public sealed class VoiceTraceLoggerTests
{
    [Fact]
    public void ProductionDoesNotProcessVerboseVoiceDiagnostics()
    {
        var sink = new CapturingLogger<VoiceTraceLogger>();
        var trace = new VoiceTraceLogger(new TestEnvironment("Production"), sink);

        trace.Log(Call(), CallerId, "connection-123", new VoiceDiagnosticReport(CallId,
            "PeerCreated", PeerGeneration: 1));

        Assert.False(trace.Enabled);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void TraceDerivesRoleAndRedactsCandidateAndSdpLikeMessages()
    {
        var sink = new CapturingLogger<VoiceTraceLogger>();
        var trace = new VoiceTraceLogger(new TestEnvironment("Development"), sink);
        const string sensitive = "candidate:1 1 udp 1 192.168.1.2 5000 typ host v=0 m=audio";

        trace.Log(Call(), CallerId, "connection-123", new VoiceDiagnosticReport(CallId,
            "IceAddFailed", PeerGeneration: 1, NegotiationGeneration: 1, CandidateType: "host",
            Protocol: "udp", ErrorName: "OperationError", SafeMessage: sensitive));

        var logged = Assert.Single(sink.Messages);
        Assert.Contains("VOICE TRACE", logged);
        Assert.Contains("Role=caller", logged);
        Assert.Contains("CandidateType=host", logged);
        Assert.Contains("[redacted potentially sensitive WebRTC payload]", logged);
        Assert.DoesNotContain("192.168.1.2", logged);
        Assert.DoesNotContain("candidate:1", logged);
        Assert.DoesNotContain("m=audio", logged);
        Assert.DoesNotContain("v=0", logged);
    }

    private static readonly Guid CallId = Guid.NewGuid();
    private static readonly Guid CallerId = Guid.NewGuid();
    private static readonly Guid CalleeId = Guid.NewGuid();

    private static CallSessionDto Call() => new(CallId, CallKind.DirectVoice, Guid.NewGuid(), CallerId,
        CallState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
        [new(CallerId, "Caller", false, false, false, DateTimeOffset.UtcNow, CallConnectionState.New),
         new(CalleeId, "Callee", false, false, false, DateTimeOffset.UtcNow, CallConnectionState.New)]);

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Iridium.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
