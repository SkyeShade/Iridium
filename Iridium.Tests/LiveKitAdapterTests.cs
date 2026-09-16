using System.Reflection;
using Iridium.Web.Services;
using Microsoft.JSInterop;

namespace Iridium.Tests;

public sealed class LiveKitAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectAdapterPassesAllWatchArgumentsInJavaScriptOrder(bool subscribed)
    {
        var module = new RecordingModule();
        var adapter = new LiveKitCallMediaService(null!, null!, null!, null!);
        Bind(adapter, module);
        await adapter.SetStreamSubscriptionAsync("stream-id", "media-id", "owner-account", subscribed);
        Assert.Equal("setStreamSubscription", module.Method);
        Assert.Equal(new object?[] { "session", "stream-id", "media-id", "owner-account", subscribed }, module.Arguments);
        await adapter.SetStreamSubscriptionAsync("legacy-media", subscribed);
        Assert.Equal(new object?[] { "session", "legacy-media", "legacy-media", null, subscribed }, module.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommunityAdapterPassesAllWatchArgumentsInJavaScriptOrder(bool subscribed)
    {
        var module = new RecordingModule();
        var adapter = new LiveKitCommunityVoiceMediaClient(null!, null!, null!);
        Bind(adapter, module);
        await adapter.SetStreamSubscriptionAsync("stream-id", "media-id", "owner-account", subscribed);
        Assert.Equal("setStreamSubscription", module.Method);
        Assert.Equal(new object?[] { "session", "stream-id", "media-id", "owner-account", subscribed }, module.Arguments);
        await adapter.SetStreamSubscriptionAsync("legacy-media", subscribed);
        Assert.Equal(new object?[] { "session", "legacy-media", "legacy-media", null, subscribed }, module.Arguments);
    }

    private static void Bind(object adapter, RecordingModule module)
    {
        adapter.GetType().GetField("_module", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, module);
        adapter.GetType().GetField("_sessionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, "session");
    }

    private sealed class RecordingModule : IJSObjectReference
    {
        public string? Method { get; private set; }
        public object?[]? Arguments { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Method = identifier; Arguments = args;
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
