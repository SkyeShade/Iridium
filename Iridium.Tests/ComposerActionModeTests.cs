using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class ComposerActionModeTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public async Task DefaultModeIsAttachmentAndStoredAvatarModeRestores()
    {
        var store = new MemoryStore();
        var service = new ComposerActionModePreferencesService(store);
        var scope = new ComposerActionModeScope("https://alpha.example/", Guid.NewGuid());

        Assert.Equal(ComposerActionMode.Attachment, await service.GetAsync(scope));
        await service.SetAsync(scope, ComposerActionMode.Avatar);
        Assert.Equal(ComposerActionMode.Avatar,
            await new ComposerActionModePreferencesService(store).GetAsync(scope));
    }

    [Fact]
    public async Task PreferenceIsScopedByNormalizedNodeAndAccount()
    {
        var store = new MemoryStore();
        var service = new ComposerActionModePreferencesService(store);
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var alphaA = new ComposerActionModeScope("HTTPS://ALPHA.EXAMPLE/", accountA);
        var normalizedAlphaA = new ComposerActionModeScope("https://alpha.example", accountA);
        var alphaB = new ComposerActionModeScope("https://alpha.example", accountB);
        var betaA = new ComposerActionModeScope("https://beta.example", accountA);

        await service.SetAsync(alphaA, ComposerActionMode.Avatar);

        Assert.Equal(ComposerActionMode.Avatar, await service.GetAsync(normalizedAlphaA));
        Assert.Equal(ComposerActionMode.Attachment, await service.GetAsync(alphaB));
        Assert.Equal(ComposerActionMode.Attachment, await service.GetAsync(betaA));
        Assert.StartsWith("iridium.composerActionMode.v1:", alphaA.StorageKey);
        Assert.Equal(3, new[] { alphaA.StorageKey, alphaB.StorageKey, betaA.StorageKey }
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task DirectMessagesForceAttachmentsWithoutOverwritingSavedAvatarMode()
    {
        var store = new MemoryStore();
        var service = new ComposerActionModePreferencesService(store);
        var scope = new ComposerActionModeScope("https://alpha.example", Guid.NewGuid());
        await service.SetAsync(scope, ComposerActionMode.Avatar);

        var saved = await service.GetAsync(scope);
        Assert.Equal(ComposerActionMode.Avatar,
            ComposerActionModePreferencesService.EffectiveMode(saved, isDirectMessage: false));
        Assert.Equal(ComposerActionMode.Attachment,
            ComposerActionModePreferencesService.EffectiveMode(saved, isDirectMessage: true));
        Assert.Equal(ComposerActionMode.Avatar, await service.GetAsync(scope));
    }

    [Fact]
    public void ComposerWiresModeActionsWithoutReplacingAttachmentFlow()
    {
        var razor = Source("Iridium.Web", "Components", "MessageComposer.razor");
        var javascript = Source("Iridium.Web", "wwwroot", "js", "chat.js");

        Assert.Contains("@oncontextmenu:preventDefault", razor);
        Assert.Contains("PerformComposerActionAsync", razor);
        Assert.Contains("? OpenFilePickerAsync()", razor);
        Assert.Contains("openComposerFilePicker", razor);
        Assert.Contains("class=\"avatar-picker-add-file\" aria-label=\"Add files\"", razor);
        Assert.Contains("AddFileFromAvatarPickerAsync", razor);
        Assert.Contains("await OpenFilePickerAsync()", razor);
        Assert.Contains("avatar-picker-section-divider", razor);
        Assert.True(razor.IndexOf(">Attachments</span>", StringComparison.Ordinal) <
                    razor.IndexOf("class=\"avatar-picker-add-file\"", StringComparison.Ordinal));
        Assert.True(razor.IndexOf("class=\"avatar-picker-add-file\"", StringComparison.Ordinal) <
                    razor.IndexOf("class=\"avatar-picker-section-divider\"", StringComparison.Ordinal));
        Assert.True(razor.IndexOf("class=\"avatar-picker-section-divider\"", StringComparison.Ordinal) <
                    razor.IndexOf(">Choose avatar</div>", StringComparison.Ordinal));
        Assert.True(razor.IndexOf(">Choose avatar</div>", StringComparison.Ordinal) <
                    razor.IndexOf("class=\"avatar-picker-strip\"", StringComparison.Ordinal));
        Assert.Contains("Session.SetCommunityProfileAsync(communityId, presetId)", razor);
        Assert.Contains("Session.GetProfilePresetsAsync(communityId)", razor);
        Assert.Contains("await OnCommunityProfileChanged.InvokeAsync()", razor);
        Assert.Contains("new QuickAvatarEntry(value.Id, value.Avatar?.Id", razor);
        Assert.Contains("Session.SetActiveAvatarPresetAsync(presetId)", razor);
        Assert.Contains("ProfileMedia.Invalidate(expectedAccount, state.AvatarRevision)", razor);
        Assert.Contains("composerActionLongPressMilliseconds = 2000", javascript);
        Assert.Contains("Math.hypot", javascript);
        Assert.Contains("pointercancel", javascript);
        Assert.Contains("event.stopImmediatePropagation()", javascript);
        Assert.Contains("window.matchMedia(\"(max-width: 860px)\")", javascript);
        Assert.Contains("default-avatar-entry", razor);
        Assert.Contains("avatar-strip-divider", razor);
        Assert.Contains("avatar-preset-scroll", razor);
        Assert.Contains("Session.SetActiveAvatarPresetAsync(null)", razor);
        Assert.Contains("Session.SetCommunityProfileAsync(communityId, null)", razor);
        Assert.Contains("BaseAvatarPreset", razor);
        Assert.Contains("wireHorizontalWheel", javascript);
        Assert.DoesNotContain("selected-check", razor);
        Assert.DoesNotContain("selected-check", Source("Iridium.Web", "Components", "MessageComposer.razor.css"));
        Assert.Contains("EffectiveActionMode", razor);
        Assert.Contains("if (IsDirectMessage) return;", razor);
        Assert.Contains("if (IsDirectMessage) return Task.CompletedTask;", razor);
        Assert.Contains("wireComposerActionButton\", _actionButton, _selfReference, !IsDirectMessage", razor);
        Assert.Contains("!enableModeSwitch", javascript);
    }

    [Fact]
    public async Task RecentAvatarUsageOrdersPresetsPerNodeAndAccount()
    {
        var store = new MemoryUsageStore();
        var clock = new TestTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var service = new ComposerAvatarUsageService(store, clock);
        var account = Guid.NewGuid();
        var scope = new ComposerActionModeScope("https://alpha.example", account);
        var otherAccount = new ComposerActionModeScope("https://alpha.example", Guid.NewGuid());
        var older = Preset(0, DateTimeOffset.UnixEpoch.AddDays(2));
        var newer = Preset(1, DateTimeOffset.UnixEpoch.AddDays(1));

        var metadataOrder = ComposerAvatarUsageService.MostRecentlyUsedFirst([newer, older], new(),
            value => value.Id, value => value.UpdatedAt);
        Assert.Equal([older.Id, newer.Id], metadataOrder.Select(value => value.Id));

        await service.RecordAsync(scope, newer.Id);
        clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(2_000);
        var usage = await service.RecordAsync(scope, older.Id);
        var recentOrder = ComposerAvatarUsageService.MostRecentlyUsedFirst([newer, older], usage,
            value => value.Id, value => value.UpdatedAt);

        Assert.Equal([older.Id, newer.Id], recentOrder.Select(value => value.Id));
        Assert.Empty((await service.GetAsync(otherAccount)).LastUsedAtUnixMilliseconds);
        Assert.StartsWith("iridium.composerAvatarUsage.v1:", ComposerAvatarUsageService.StorageKey(scope));
    }

    private static AccountAvatarPresetDto Preset(int slot, DateTimeOffset updatedAt) => new(
        Guid.NewGuid(), slot, $"https://alpha.example/avatar/{slot}", 1, "image/png", 64, 64,
        0, 0, 1, updatedAt, updatedAt);

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private sealed class MemoryStore : IComposerActionModeStore
    {
        private readonly Dictionary<string, ComposerActionMode> _values = new(StringComparer.Ordinal);

        public Task<ComposerActionMode?> LoadAsync(ComposerActionModeScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(scope.StorageKey, out var mode) ? (ComposerActionMode?)mode : null);

        public Task SaveAsync(ComposerActionModeScope scope, ComposerActionMode mode,
            CancellationToken cancellationToken = default)
        {
            _values[scope.StorageKey] = mode;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryUsageStore : IComposerAvatarUsageStore
    {
        private readonly Dictionary<string, ComposerAvatarUsageData> _values = new(StringComparer.Ordinal);

        public Task<ComposerAvatarUsageData> LoadAsync(ComposerActionModeScope scope,
            CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(ComposerAvatarUsageService.StorageKey(scope))
            ?? new());

        public Task SaveAsync(ComposerActionModeScope scope, ComposerAvatarUsageData usage,
            CancellationToken cancellationToken = default)
        {
            _values[ComposerAvatarUsageService.StorageKey(scope)] = usage;
            return Task.CompletedTask;
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
