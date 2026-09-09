namespace Iridium.Tests;

public sealed class VoiceInputSensitivityUiTests
{
    [Fact]
    public void SettingsUsesOnePersistedPreferenceAndConditionalManualControl()
    {
        var settings = Source("Iridium.Web", "Components", "VoiceVideoSettings.razor");
        var preferences = Source("Iridium.Client.Core", "LocalVoicePreferences.cs");
        var storage = Source("Iridium.Web", "Services", "BrowserClientStorage.cs");

        Assert.Contains("Preferences.AutoInputSensitivity", settings);
        Assert.Contains("ManualInputSensitivityThreshold", settings);
        Assert.Contains("@if (Preferences.AutoInputSensitivity)", settings);
        Assert.Contains("type=\"range\"", settings);
        Assert.Contains("SetAutoInputSensitivityAsync", settings);
        Assert.Contains("SetManualInputSensitivityThresholdAsync", settings);
        Assert.Contains("AutoInputSensitivity = true", preferences);
        Assert.Contains("ManualInputSensitivityThreshold = 0.5", preferences);
        Assert.Contains("iridium.voicePreferences.v1", storage);
    }

    [Fact]
    public void MeterRebindsToSelectedDeviceAndIsDisposedWithSettingsComponent()
    {
        var settings = Source("Iridium.Web", "Components", "VoiceVideoSettings.razor");
        var meter = Source("Iridium.Web", "wwwroot", "js", "microphoneInputMeter.js");

        Assert.Contains("rebindMicrophoneInputMeter", settings);
        Assert.Contains("SetInputDeviceAsync", settings);
        Assert.Contains("stopMicrophoneInputMeter", settings);
        Assert.Contains("getUserMedia(microphoneConstraints(deviceId))", meter);
        Assert.Contains("releaseGraph(meter)", meter);
        Assert.Contains("track.stop()", meter);
        Assert.Contains("requestAnimationFrame(sample)", meter);
    }

    [Fact]
    public void SettingsJsInitializationAndDisposalHaveSingleInstanceOwnership()
    {
        var settings = Source("Iridium.Web", "Components", "VoiceVideoSettings.razor");
        var render = Slice(settings, "protected override async Task OnAfterRenderAsync", "[JSInvokable]");
        var dispose = Slice(settings, "public async ValueTask DisposeAsync", "private static async ValueTask DisposeModuleAsync");

        Assert.Contains("if (!firstRender || _disposed) return", render);
        Assert.Contains("IJSObjectReference? importedModule = null", render);
        Assert.True(render.IndexOf("await JS.InvokeAsync<IJSObjectReference>", StringComparison.Ordinal) <
                    render.IndexOf("if (_disposed)", StringComparison.Ordinal));
        Assert.Contains("await DisposeModuleAsync(abandonedModule)", render);
        Assert.Contains("_module = importedModule", render);
        Assert.Contains("if (_disposed || _lifetime.IsCancellationRequested) return", render);
        Assert.Contains("catch (OperationCanceledException) when (_disposed", render);
        Assert.Contains("catch (ObjectDisposedException) when (_disposed)", render);

        Assert.True(dispose.IndexOf("_disposed = true", StringComparison.Ordinal) <
                    dispose.IndexOf("_lifetime.Cancel()", StringComparison.Ordinal));
        Assert.True(dispose.IndexOf("Preferences.Changed -= PreferencesChanged", StringComparison.Ordinal) <
                    dispose.IndexOf("stopMicrophoneInputMeter", StringComparison.Ordinal));
        Assert.Contains("_module = null", dispose);
        Assert.Contains("_meterId = null", dispose);
        Assert.Contains("_callback = null", dispose);
        Assert.True(dispose.IndexOf("stopMicrophoneInputMeter", StringComparison.Ordinal) <
                    dispose.IndexOf("callback?.Dispose()", StringComparison.Ordinal));
        Assert.True(dispose.IndexOf("callback?.Dispose()", StringComparison.Ordinal) <
                    dispose.IndexOf("DisposeModuleAsync(module)", StringComparison.Ordinal));
    }

    [Fact]
    public void LateSettingsEventsCannotInvokeJsOrRenderAfterDisposal()
    {
        var settings = Source("Iridium.Web", "Components", "VoiceVideoSettings.razor");
        var input = Slice(settings, "private async Task InputDeviceChangedAsync", "private void PreferencesChanged");
        var callback = Slice(settings, "public Task OnMicrophoneLevel", "private async Task AutomaticChangedAsync");

        Assert.Contains("if (_disposed) return", input);
        Assert.Contains("var module = _module", input);
        Assert.Contains("if (_disposed || _lifetime.IsCancellationRequested) return", input);
        Assert.Contains("if (_disposed) return Task.CompletedTask", callback);
        Assert.Contains("return InvokeAsync(() =>", callback);
        Assert.Contains("if (_disposed) return", callback);
        Assert.Contains("if (!_disposed) StateHasChanged()", settings);
    }

    [Fact]
    public void EveryVoiceMediaPathReceivesAndAppliesSensitivityChanges()
    {
        foreach (var file in new[]
                 {
                     "LiveKitCallMediaService.cs", "LiveKitCommunityVoiceMediaClient.cs",
                     "WebRtcCallMediaService.cs", "BrowserCommunityVoiceMediaClient.cs"
                 })
        {
            var source = Source("Iridium.Web", "Services", file);
            Assert.Contains("localVoicePreferences.Current", source);
            Assert.Contains("setInputSensitivity", source);
            Assert.Contains("localVoicePreferences.Changed", source);
        }
    }

    private static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string Slice(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first);
        return source[first..last];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
