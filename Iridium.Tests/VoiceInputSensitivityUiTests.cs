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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
