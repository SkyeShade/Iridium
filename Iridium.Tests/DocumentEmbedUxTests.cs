using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class DocumentEmbedUxTests
{
    [Fact]
    public void UnsetPreferenceUsesResponsiveDefaultAndExplicitChoicesOverrideIt()
    {
        Assert.True(DocumentEmbedPreferencesService.EffectiveAutoLoad(null, isMobileLayout: false));
        Assert.False(DocumentEmbedPreferencesService.EffectiveAutoLoad(null, isMobileLayout: true));
        Assert.True(DocumentEmbedPreferencesService.EffectiveAutoLoad(true, isMobileLayout: true));
        Assert.False(DocumentEmbedPreferencesService.EffectiveAutoLoad(false, isMobileLayout: false));
    }

    [Fact]
    public async Task PreferencePersistsPerNormalizedNodeAndAccount()
    {
        var store = new MemoryStore();
        var service = new DocumentEmbedPreferencesService(store);
        var account = Guid.NewGuid();
        var scope = new DocumentEmbedPreferenceScope("HTTPS://NODE.EXAMPLE/", account);

        Assert.Null(await service.GetStoredAsync(scope));
        await service.SetAsync(scope, false);

        Assert.False(await new DocumentEmbedPreferencesService(store).GetStoredAsync(
            new("https://node.example", account)));
        Assert.Null(await new DocumentEmbedPreferencesService(store).GetStoredAsync(
            new("https://node.example", Guid.NewGuid())));
        Assert.StartsWith("iridium.documentEmbedAutoLoad.v1:", scope.StorageKey);
    }

    [Fact]
    public void DisabledHostsGateProviderRequestsAndHeavyRenderersBehindManualLoad()
    {
        var channel = Source("Iridium.Web", "Components", "ChannelView.razor");
        var preview = Source("Iridium.Web", "Components", "MessageDocumentPreview.razor");
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");

        Assert.Contains("@if (!EmbedContentMounted)", channel);
        Assert.Contains("@if (!EmbedContentMounted)", preview);
        Assert.Contains("LoadThisEmbedAsync", channel);
        Assert.Contains("LoadThisEmbedAsync", preview);
        Assert.Contains("if (_embedAutoLoad) StartEmbedLoad", channel);
        Assert.Contains("if (_wireVisibility && EmbedContentMounted)", preview);
        Assert.True(channel.IndexOf("@if (!EmbedContentMounted)", StringComparison.Ordinal) <
                    channel.IndexOf("<EmbeddedDocumentView", StringComparison.Ordinal));
        Assert.True(preview.IndexOf("@if (!EmbedContentMounted)", StringComparison.Ordinal) <
                    preview.IndexOf("<EmbeddedDocumentView", StringComparison.Ordinal));
        Assert.Contains("NodeAuthority=\"NodeAuthority\" AccountId=\"CurrentAccountId\"", row);
        Assert.Contains("IsMobileLayout=\"IsMobileLayout\"", row);
    }

    [Fact]
    public void DocsUseTheOriginalNaturalFlowWithoutAnyZoomStructure()
    {
        var document = Source("Iridium.Web", "Components", "EmbeddedDocumentView.razor");
        var channel = Source("Iridium.Web", "Components", "ChannelView.razor");
        var preview = Source("Iridium.Web", "Components", "MessageDocumentPreview.razor");

        Assert.Contains("<article @ref=\"_root\" class=\"embedded-document\" aria-label=\"Imported document\">", document);
        Assert.DoesNotContain("EmbedZoomControls", document);
        Assert.DoesNotContain("embed-zoom", document);
        Assert.DoesNotContain("_zoom", document);
        Assert.DoesNotContain("wireEmbedZoom", document);
        Assert.False(File.Exists(Path.Combine(Root, "Iridium.Web", "Components", "EmbeddedDocumentView.razor.css")));
        Assert.Contains("<EmbeddedDocumentView", channel);
        Assert.Contains("<EmbeddedDocumentView", preview);
        Assert.Contains("LimitCollapsedPreview=\"@(!_expanded)\"", preview);
        Assert.Contains("Expand document", preview);
        Assert.Contains("Collapse document", preview);
    }

    [Fact]
    public void SheetZoomUsesOneTransformedGeometrySurfaceWithExplicitScrollExtents()
    {
        var controls = Source("Iridium.Web", "Components", "EmbedZoomControls.razor");
        var sheet = Source("Iridium.Web", "Components", "EmbeddedSheetView.razor");
        var sheetCss = Source("Iridium.Web", "Components", "EmbeddedSheetView.razor.css");
        var javascript = Source("Iridium.Web", "wwwroot", "js", "chat.js");

        foreach (var preset in new[] { ".5", ".67", ".8", ".9", "1", "1.1", "1.25", "1.5", "2" })
            Assert.Contains(preset, controls);
        Assert.Contains("SupportsFitWidth=\"true\"", sheet);
        Assert.Contains("Math.Clamp(zoom, .25, 1)", sheet);
        Assert.Contains("data-embed-zoom-content", sheet);
        Assert.Contains("transform:scale(var(--embed-zoom))", sheetCss);
        Assert.DoesNotContain("zoom:", sheetCss);
        Assert.Contains("layout.style.width = `${sourceWidth * zoom}px`", javascript);
        Assert.Contains("layout.style.height = `${sourceHeight * zoom}px`", javascript);
        Assert.Contains("new ResizeObserver", javascript);
        Assert.Contains("viewport.clientWidth / fixedSourceWidth", javascript);
        Assert.Contains("observer.observe(viewport)", javascript);
        Assert.Contains("sheet-floating-image", sheet);
        Assert.True(sheet.IndexOf("sheet-canvas embed-zoom-content", StringComparison.Ordinal) <
                    sheet.IndexOf("sheet-floating-image", StringComparison.Ordinal));
    }

    [Fact]
    public void SheetTabsAndZoomShareOneResponsiveToolbar()
    {
        var sheet = Source("Iridium.Web", "Components", "EmbeddedSheetView.razor");
        var sheetCss = Source("Iridium.Web", "Components", "EmbeddedSheetView.razor.css");
        var controlsCss = Source("Iridium.Web", "Components", "EmbedZoomControls.razor.css");
        var toolbar = Slice(sheet, "<div class=\"sheet-toolbar", "@if (_renderTab");

        Assert.True(toolbar.IndexOf("<nav class=\"sheet-tabs\"", StringComparison.Ordinal) <
                    toolbar.IndexOf("<EmbedZoomControls", StringComparison.Ordinal));
        Assert.Contains("Sheet.Tabs.Count > 1", toolbar);
        Assert.Contains("sheet-toolbar.zoom-only", sheetCss);
        Assert.Contains("flex-flow:row nowrap", sheetCss);
        Assert.Contains("flex:1 1 auto", sheetCss);
        Assert.Contains("min-width:0", sheetCss);
        Assert.Contains("overflow-x:auto", sheetCss);
        Assert.Contains("flex:0 0 auto", controlsCss);
        Assert.DoesNotContain("border-bottom", controlsCss);
    }

    [Fact]
    public void ZoomStateSurvivesPreviewCollapseRefreshAndSheetTabSwitches()
    {
        var preview = Source("Iridium.Web", "Components", "MessageDocumentPreview.razor");
        var sheet = Source("Iridium.Web", "Components", "EmbeddedSheetView.razor");

        Assert.Equal("private string RenderKey => _sourceKey ?? string.Empty;", preview.Split('\n')
            .Select(value => value.Trim()).Single(value => value.StartsWith("private string RenderKey")));
        Assert.DoesNotContain("_zoom = 1", Slice(sheet, "private void SelectTab", "private void ResetProgressiveRender"));
        Assert.DoesNotContain("_zoom = 1", Slice(sheet, "private void ResetProgressiveRender", "private void PrepareNextBatch"));
    }

    private static string Slice(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first);
        return source[first..last];
    }

    private sealed class MemoryStore : IDocumentEmbedPreferenceStore
    {
        private readonly Dictionary<string, bool> _values = [];
        public Task<bool?> LoadAsync(DocumentEmbedPreferenceScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(scope.StorageKey, out var value) ? (bool?)value : null);
        public Task SaveAsync(DocumentEmbedPreferenceScope scope, bool autoLoad,
            CancellationToken cancellationToken = default)
        {
            _values[scope.StorageKey] = autoLoad;
            return Task.CompletedTask;
        }
    }

    private static readonly string Root = FindRoot();
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
    private static string FindRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
