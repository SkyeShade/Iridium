using System.Net;
using System.IO.Compression;
using System.Text;
using System.Runtime.CompilerServices;
using Iridium.Protocol;
using Iridium.Server.Embeds;
using Iridium.UI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iridium.Tests;

public sealed class GoogleSheetsEmbedTests
{
    private const string SheetId = "sheet_abcdefghij123";

    [Fact]
    public void UrlOnlyResolutionDetectsProviderAndCanonicalizesSources()
    {
        Assert.True(CommunityChannelEmbeds.TryResolveContent(
            "https://docs.google.com/document/d/doc_abcdefghij123/edit?usp=sharing", out var document));
        Assert.Equal(CommunityChannelEmbedProvider.GoogleDocs, document!.Provider);
        Assert.True(CommunityChannelEmbeds.TryResolveContent(
            $"https://docs.google.com/spreadsheets/d/{SheetId}/edit?usp=sharing#gid=42", out var sheet));
        Assert.Equal(CommunityChannelEmbedProvider.GoogleSheets, sheet!.Provider);
        Assert.Equal("42", sheet.TabId);
        Assert.False(CommunityChannelEmbeds.TryResolveContent("https://docs.google.com/forms/d/nope/edit", out _));
    }

    [Fact]
    public void SheetColorsPreserveReadableForegroundAndAdaptOnlyEmptyWhiteStructure()
    {
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Foreground("#ffffff", "#1155CC"));
        Assert.Equal("#000000", EmbeddedSheetColors.Foreground("#000000", "#FFFFFF"));
        Assert.Equal("#00AACC", EmbeddedSheetColors.Foreground("#00aacc", "#101820"));
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Background("#ffffff", hasContent: true, isMerged: false));
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Background("#ffffff", hasContent: false, isMerged: true));
        var structural = EmbeddedSheetColors.Background("#ffffff", hasContent: false, isMerged: false);
        Assert.Equal("#292D35", structural);
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Foreground("#000000", structural, backgroundTransformed: true));
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Foreground("#FFFFFF", "#C9DAF8"));
        Assert.Equal("#000000", EmbeddedSheetColors.Foreground(null, "#FFFFFF"));
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Foreground(null, "#073763"));
        Assert.Equal("#1155CC", EmbeddedSheetColors.Background("#1155CC", hasContent: false, isMerged: false));
        Assert.Equal("#FFFFFF", EmbeddedSheetColors.Border("#FFFFFF"));
        Assert.Equal("#F2F2F2", EmbeddedSheetColors.Background("#f2f2f2", hasContent: false, isMerged: false));
    }

    [Fact]
    public void SettingsSurfacesInferProviderAndServerIgnoresClientProviderMismatch()
    {
        var root = Root();
        var files = new[]
        {
            Path.Combine(root, "Iridium.Web", "Components", "ChannelSettingsDialog.razor"),
            Path.Combine(root, "Iridium.Web", "Components", "CommunityPermissionEditor.razor"),
            Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor")
        };
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain(">Provider<", source);
            Assert.Contains("Google URL", source);
            Assert.Contains("Detected:", source);
        }
        Assert.Contains("TryResolveContent(embed.Url", File.ReadAllText(Path.Combine(root,
            "Iridium.Server", "Api", "CommunityStructureEndpoints.cs")));
        Assert.Contains("TryResolveContent(embed.Url", File.ReadAllText(Path.Combine(root,
            "Iridium.Server", "Api", "CommunityForumEndpoints.cs")));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("view")]
    [InlineData("preview")]
    public void RecognizesCanonicalAnonymousSheetLinks(string action)
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleSheets(
            $"https://docs.google.com/spreadsheets/d/{SheetId}/{action}?gid=42&usp=sharing", out var source));
        Assert.Equal(SheetId, source!.SpreadsheetId);
        Assert.Equal("42", source.Gid);
        Assert.Equal($"https://docs.google.com/spreadsheets/d/{SheetId}/export?format=xlsx", source.FetchUrl);
        Assert.DoesNotContain("usp", source.FetchUrl);
    }

    [Theory]
    [InlineData("http://docs.google.com/spreadsheets/d/sheet_abcdefghij123/edit")]
    [InlineData("https://evil.example/spreadsheets/d/sheet_abcdefghij123/edit")]
    [InlineData("https://docs.google.com/spreadsheets/d/short/edit")]
    [InlineData("https://docs.google.com/spreadsheets/d/sheet_abcdefghij123/edit?gid=bad")]
    public void RejectsMalformedOrUnsafeSheetLinks(string url) =>
        Assert.False(CommunityChannelEmbeds.TryGoogleSheets(url, out _));

    [Fact]
    public void RecognizesPublishedSheetWithoutPreservingArbitraryQueryParameters()
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleSheets(
            "https://docs.google.com/spreadsheets/d/e/2PACX-sheet_abcdefghij/pubhtml?gid=9&single=true", out var source));
        Assert.Equal("https://docs.google.com/spreadsheets/d/e/2PACX-sheet_abcdefghij/pubhtml?gid=9", source!.FetchUrl);
    }

    [Theory]
    [InlineData($"https://docs.google.com/spreadsheets/d/{SheetId}/edit?usp=sharing#gid=123")]
    [InlineData($"https://docs.google.com/spreadsheets/d/{SheetId}/view#gid=123")]
    public void PreservesGidFromFragment(string url)
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleSheets(url, out var source));
        Assert.Equal("123", source!.Gid);
        Assert.EndsWith("?gid=123", source.OpenUrl);
        Assert.EndsWith("export?format=xlsx", source.FetchUrl);
    }

    [Fact]
    public void ShareLinkWithoutGidLetsGoogleSelectTheFirstVisibleTab()
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleSheets(
            $"https://docs.google.com/spreadsheets/d/{SheetId}/edit?usp=sharing", out var source));
        Assert.Null(source!.Gid);
        Assert.Equal($"https://docs.google.com/spreadsheets/d/{SheetId}/export?format=xlsx", source.FetchUrl);
    }

    [Fact]
    public void MixedMessageSourcesShareOneThreeItemCapAndDedupeCanonicalIdentity()
    {
        var sources = CommunityChannelEmbeds.FindSupportedContent($"""
            https://docs.google.com/document/d/document_abcdef/edit
            https://docs.google.com/spreadsheets/d/{SheetId}/edit?gid=0
            https://docs.google.com/spreadsheets/d/{SheetId}/view?gid=0
            https://docs.google.com/spreadsheets/d/second_sheet_abcdef/edit
            https://docs.google.com/document/d/fourth_document_abc/edit
            """);
        Assert.Equal(3, sources.Count);
        Assert.Equal([CommunityChannelEmbedProvider.GoogleDocs, CommunityChannelEmbedProvider.GoogleSheets,
            CommunityChannelEmbedProvider.GoogleSheets], sources.Select(value => value.Provider));
    }

    [Fact]
    public void ParserNormalizesTabsMergedCellsFormattingDimensionsLinksBordersAndCheckboxes()
    {
        var html = """
            <html><head><title>Character Sheet</title><style>
            .header{font-weight:700;font-size:28px;text-align:center;background-color:#1155cc;color:#ffffff;border-bottom:2px solid #000}
            .skill{font-style:italic;text-decoration:underline;vertical-align:top;border-left:1px dashed #000}
            </style></head><body>
            <table class="waffle" data-gid="0" data-sheet-name="Character"><col style="width:180px"><col style="width:90px">
            <tr style="height:36px"><td class="header" colspan="2">Vitality Points</td></tr>
            <tr><td class="skill"><a href="https://example.com/skill">Skills</a></td><td><input type="checkbox" checked></td></tr></table>
            <table class="waffle" data-gid="7" data-sheet-name="Inventory"><tr><td>Equipment</td></tr></table>
            </body></html>
            """;
        var source = SheetSource("7");
        var sheet = new GoogleSheetsHtmlParser().Parse(html, source)!;
        Assert.Equal("7", sheet.DefaultTabId);
        Assert.Equal(2, sheet.Tabs.Count);
        var character = sheet.Tabs[0];
        Assert.Equal([180, 90], character.ColumnWidths);
        Assert.Equal(36, character.Rows[0].Height);
        var header = Assert.Single(character.Rows[0].Cells);
        Assert.Equal(2, header.ColumnSpan);
        Assert.True(header.Bold);
        Assert.Equal(EmbeddedDocumentTextAlignment.Center, header.HorizontalAlignment);
        Assert.Equal(EmbeddedSheetCellColor.Blue, header.BackgroundColor);
        Assert.Equal("#1155CC", header.BackgroundHex);
        Assert.Equal("#FFFFFF", header.ForegroundHex);
        Assert.Equal(EmbeddedSheetFontSize.Heading, header.FontSize);
        Assert.Equal(EmbeddedSheetBorderStyle.Medium, header.BottomBorder);
        Assert.Equal("#000000", header.BottomBorderColor);
        var skill = character.Rows[1].Cells[0];
        Assert.True(skill.Italic && skill.Underline);
        Assert.Equal("https://example.com/skill", skill.Link);
        Assert.Equal(EmbeddedSheetBorderStyle.Dashed, skill.LeftBorder);
        Assert.True(character.Rows[1].Cells[1].CheckboxValue);
    }

    [Fact]
    public void HtmlParserPreservesExplicitWhiteFromCellAndNestedGeneratedClass()
    {
        var sheet = new GoogleSheetsHtmlParser().Parse("""
            <style>.pale{background-color:#c9daf8}.white{color:#fff}.black{color:#000}</style>
            <table><tr><td class="pale white">Alysam Hyoka</td>
            <td style="background:#c9daf8"><span class="white">@arysamk1</span></td>
            <td style="background:#fff"><span class="black">Origin</span></td></tr></table>
            """, SheetSource("0"))!;
        var cells = sheet.Tabs[0].Rows[0].Cells;
        Assert.Equal("#FFFFFF", cells[0].ForegroundHex);
        Assert.Equal("#FFFFFF", cells[1].ForegroundHex);
        Assert.Equal("#000000", cells[2].ForegroundHex);
    }

    [Fact]
    public void FormattedHtmlDisplayStringsRemainAuthoritativeAcrossNumberKinds()
    {
        var display = new GoogleSheetsHtmlParser().Parse("""
            <table><tr><td>20</td><td>19.50</td><td>3.4%</td><td>$5.744,00</td>
            <td>31/08/2026</td><td>0019.500 kg</td><td>42</td></tr></table>
            """, SheetSource("0"))!.Tabs[0].Rows[0].Cells.Select(cell => cell.DisplayValue).ToArray();
        Assert.Equal(["20", "19.50", "3.4%", "$5.744,00", "31/08/2026", "0019.500 kg", "42"], display);
    }

    [Fact]
    public async Task ProviderCachesByCanonicalSheetIdentityAndManualRefreshUpdatesFingerprint()
    {
        var handler = new SequenceHandler([
            Html("<table data-gid='0'><tr><td>Vitality Points</td></tr></table>"),
            Html("<table data-gid='0'><tr><td>Mana Points</td></tr></table>")
        ]);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(http, cache);
        var source = SheetSource("0");
        var first = await service.GetAsync(source, default);
        var forumHost = await service.GetAsync(source, default);
        var messageHost = await service.GetAsync(source, default);
        var directMessageHost = await service.GetAsync(source, default);
        Assert.Same(first, forumHost);
        Assert.Same(first, messageHost);
        Assert.Same(first, directMessageHost);
        Assert.Equal(1, handler.RequestCount);
        var refreshed = await service.RefreshAsync(source, default);
        Assert.NotEqual(first.ContentVersion, refreshed.ContentVersion);
        Assert.Equal("Mana Points", refreshed.Sheet!.Tabs[0].Rows[0].Cells[0].DisplayValue);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task NormalShareLinkFollowsOnlyGoogleWorkbookExportAndParsesFormattedXlsx()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new("https://doc-01-sheets.googleusercontent.com/export/example");
        var workbook = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LayoutWorkbook()) };
        workbook.Content.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var handler = new SequenceHandler([redirect, workbook, Csv("""
            The Renaissance of the Legends,,
            ,,
            22,,TRUE
            Alysam Hyoka,,
            42,,
            """)]);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(CommunityChannelEmbeds.TryGoogleSheets(
            $"https://docs.google.com/spreadsheets/d/{SheetId}/edit?usp=sharing", out var configuration));
        var result = await Service(http, cache).GetAsync(configuration!.ToContent(), default);
        Assert.Equal(ChannelEmbedDocumentStatus.Ready, result.Status);
        Assert.Equal("The Renaissance of the Legends", result.Sheet!.Tabs[0].Rows[0].Cells[0].DisplayValue);
        Assert.Equal("22", result.Sheet.Tabs[0].Rows[2].Cells[0].DisplayValue);
        Assert.Equal("21.5", result.Sheet.Tabs[0].Rows[2].Cells[0].RawValue);
        var image = Assert.Single(result.Sheet.Tabs[0].Images!);
        Assert.NotNull(await Service(http, cache).GetMediaAsync(configuration.ToContent(), image.MediaId, default));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task StaleSheetReturnsLastGoodWhileOneSharedRefreshUpdatesCache()
    {
        var handler = new SequenceHandler([
            Html("<table data-gid='0'><tr><td>Cached</td></tr></table>"),
            Html("<table data-gid='0'><tr><td>Updated</td></tr></table>")
        ]);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(http, cache); var source = SheetSource("0");
        var first = await service.GetAsync(source, default);
        cache.Remove($"embedded-content:{source.CacheIdentity}");
        var stale = await service.GetAsync(source, default);
        Assert.True(stale.IsStale);
        Assert.Equal(first.ContentVersion, stale.ContentVersion);
        var fresh = await service.GetAsync(source, default);
        Assert.False(fresh.IsStale);
        Assert.Equal("Updated", fresh.Sheet!.Tabs[0].Rows[0].Cells[0].DisplayValue);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ProviderDiscoversAndLoadsMultipleVisibleTabsWithRequestedDefault()
    {
        var first = Html("""
            <a href='?gid=0'>Character</a><a href='?gid=7'>Inventory</a>
            <table data-gid='7'><tr><td>Inventory content</td></tr></table>
            """);
        var second = Html("<table data-gid='0'><tr><td>Character content</td></tr></table>");
        using var http = new HttpClient(new SequenceHandler([first, second])) { Timeout = Timeout.InfiniteTimeSpan };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await Service(http, cache).GetAsync(SheetSource("7"), default);
        Assert.Equal("7", result.Sheet!.DefaultTabId);
        Assert.Equal(["Character", "Inventory"], result.Sheet.Tabs.Select(value => value.Name));
    }

    [Fact]
    public async Task ProviderClassifiesAuthenticationTimeoutAndUnsupportedResponses()
    {
        foreach (var (response, expected) in new[]
                 {
                     (Html("<html><body>You need access</body></html>"), ChannelEmbedDocumentStatus.AuthenticationRequired),
                     (new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not html") },
                         ChannelEmbedDocumentStatus.Unsupported)
                 })
        {
            using var http = new HttpClient(new SequenceHandler([response])) { Timeout = Timeout.InfiniteTimeSpan };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            Assert.Equal(expected, (await Service(http, cache).GetAsync(SheetSource("0"), default)).Status);
        }
        using var timeoutHttp = new HttpClient(new AsyncHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Html("<table><tr><td>late</td></tr></table>");
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeoutCache = new MemoryCache(new MemoryCacheOptions());
        var settings = new GoogleDocsImportSettings(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        Assert.Equal(ChannelEmbedDocumentStatus.Timeout,
            (await Service(timeoutHttp, timeoutCache, settings).GetAsync(SheetSource("0"), default)).Status);
    }

    [Fact]
    public void ParserRendersWideValuesWhenFormattingIsAbsent()
    {
        var cells = string.Concat(Enumerable.Range(0, 250).Select(index => $"<td>{index}</td>"));
        var sheet = new GoogleSheetsHtmlParser().Parse($"<table><tr>{cells}</tr></table>", SheetSource("0"))!;
        Assert.Equal(250, Assert.Single(sheet.Tabs).Rows[0].Cells.Count);
        Assert.Equal("249", sheet.Tabs[0].Rows[0].Cells[^1].DisplayValue);
    }

    [Fact]
    public void XlsxParserPreservesSparseCoordinatesFormattedEmptyCellsMergesSizingAndStyles()
    {
        var sheet = new GoogleSheetsXlsxParser().Parse(LayoutWorkbook(), SheetSource("0"))!;
        var tab = Assert.Single(sheet.Tabs);
        Assert.Equal([61, 215, 40], tab.ColumnWidths);
        Assert.Equal(40, tab.Rows[0].Height);
        var title = Assert.Single(tab.Rows[0].Cells);
        Assert.Equal((0, 0, 1, 3), (title.Row, title.Column, title.RowSpan, title.ColumnSpan));
        Assert.Equal("The Renaissance of the Legends", title.DisplayValue);
        Assert.True(title.Bold);
        Assert.Equal(EmbeddedSheetFontSize.Heading, title.FontSize);
        Assert.Equal(EmbeddedDocumentTextAlignment.Center, title.HorizontalAlignment);
        Assert.Equal("#1155CC", title.BackgroundHex);
        Assert.Equal("#FFFFFF", title.ForegroundHex);
        Assert.Equal("Arial", title.FontFamily);
        Assert.Equal(22 * 96d / 72d, title.FontSizePx);
        Assert.Equal(700, title.FontWeight);
        Assert.Equal(EmbeddedSheetBorderStyle.Medium, title.BottomBorder);
        Assert.Equal("#073763", title.BottomBorderColor);
        Assert.Equal(1, title.SourceStyleId);

        var spacerRow = tab.Rows[1];
        Assert.Equal([0, 1, 2], spacerRow.Cells.Select(cell => cell.Column));
        Assert.Equal(string.Empty, spacerRow.Cells[1].DisplayValue);
        Assert.Equal("#C9DAF8", spacerRow.Cells[2].BackgroundHex);
        Assert.Equal(EmbeddedSheetVerticalAlignment.Top, spacerRow.Cells[2].VerticalAlignment);

        var name = tab.Rows[3].Cells[0];
        Assert.Equal("Alysam Hyoka", name.DisplayValue);
        Assert.Equal("#C9DAF8", name.BackgroundHex);
        Assert.Equal("#FFFFFF", name.ForegroundHex);
        Assert.Equal(EmbeddedDocumentTextAlignment.Start, name.HorizontalAlignment);
        Assert.Equal(EmbeddedSheetVerticalAlignment.Bottom, name.VerticalAlignment);

        Assert.Equal("22", tab.Rows[2].Cells[0].DisplayValue);
        Assert.Equal("21.5", tab.Rows[2].Cells[0].RawValue);
        Assert.Equal(EmbeddedDocumentTextAlignment.Center, tab.Rows[2].Cells[0].HorizontalAlignment);
        Assert.Equal(EmbeddedSheetVerticalAlignment.Middle, tab.Rows[2].Cells[0].VerticalAlignment);
        Assert.True(tab.Rows[2].Cells[2].IsCheckbox);
        Assert.True(tab.Rows[2].Cells[2].CheckboxValue);
        Assert.Equal(EmbeddedDocumentTextAlignment.Center, tab.Rows[2].Cells[2].HorizontalAlignment);
        Assert.Equal(EmbeddedDocumentTextAlignment.End, tab.Rows[4].Cells[0].HorizontalAlignment);
        Assert.Equal(EmbeddedSheetVerticalAlignment.Middle, title.VerticalAlignment);
        Assert.Equal(20, spacerRow.Height);
        Assert.Equal(8, tab.Rows[4].Height);
        Assert.Equal(2, tab.Rows[4].Cells[0].IndentLevel);
        Assert.Equal(4, tab.Rows[4].Cells[0].SourceStyleId);
        Assert.Equal(EmbeddedSheetBorderStyle.Double, tab.Rows[4].Cells[0].TopBorder);
        Assert.Equal(EmbeddedSheetBorderStyle.Medium, title.BottomBorder);
        Assert.Equal(EmbeddedSheetBorderStyle.Thick, title.RightBorder);
        Assert.Equal("#111111", title.RightBorderColor);
        Assert.Equal(EmbeddedSheetBorderStyle.Thin, spacerRow.Cells[2].TopBorder);
        Assert.Equal("#CC0000", spacerRow.Cells[2].TopBorderColor);
        Assert.Equal(EmbeddedSheetBorderStyle.Thin, spacerRow.Cells[2].LeftBorder);
        Assert.Equal("#00AA00", spacerRow.Cells[2].LeftBorderColor);
        Assert.False(tab.ShowGridLines);

        var centeredValueStyle = EmbeddedSheetCellPresentation.Style(tab.Rows[2].Cells[0], 20);
        Assert.Contains("text-align:center", centeredValueStyle);
        Assert.Contains("vertical-align:middle", centeredValueStyle);
        var mergedTitleStyle = EmbeddedSheetCellPresentation.Style(title, 40);
        Assert.Contains("text-align:center", mergedTitleStyle);
        Assert.Contains("vertical-align:middle", mergedTitleStyle);
        Assert.Contains("border-right:3px solid #111111", mergedTitleStyle);
        Assert.Contains("border-bottom:2px solid #073763", mergedTitleStyle);

        var parsed = new GoogleSheetsXlsxParser().ParseWithMedia(LayoutWorkbook(), SheetSource("0"))!;
        var image = Assert.Single(parsed.Sheet.Tabs[0].Images!);
        Assert.Equal((1, 1, 10, 0, 245, 60), (image.AnchorRow, image.AnchorColumn,
            image.OffsetX, image.OffsetY, image.Width, image.Height));
        Assert.True(parsed.Media.ContainsKey(image.MediaId));
    }

    [Fact]
    public void RepresentativeRpRegionsRetainSourceCoordinatesAndMergedDimensions()
    {
        var tab = Assert.Single(new GoogleSheetsXlsxParser()
            .Parse(RepresentativeRpGeometryWorkbook(), SheetSource("0"))!.Tabs);
        Assert.Equal(113, tab.ColumnWidths.Count);
        Assert.All(tab.ColumnWidths, width => Assert.InRange(width, 27, 29));
        Assert.Equal(28, tab.ColumnWidths[2]);
        Assert.All(tab.Rows, row => Assert.Equal(21, row.Height));

        AssertBox(tab, "The Renaissance of the Legends", 56, 0, 840, 42);
        AssertBox(tab, "Alysam Hyoka", 224, 84, 728, 42);
        AssertBox(tab, "Player", 980, 84, 84, 21);
        AssertBox(tab, "Origin", 980, 105, 84, 21);
        AssertBox(tab, "Level", 1344, 84, 112, 21);
        AssertBox(tab, "Experience", 1344, 105, 112, 21);
        AssertBox(tab, "Vitality", 56, 252, 252, 42);
        AssertBox(tab, "Mana", 336, 252, 252, 42);
        AssertBox(tab, "Energy", 616, 252, 252, 42);
        AssertBox(tab, "Sanity", 896, 252, 252, 42);
        AssertBox(tab, "Evasion", 1176, 147, 224, 42);
        AssertBox(tab, "Elusion", 1176, 315, 224, 42);
        AssertBox(tab, "Others", 1428, 252, 168, 42);
        AssertBox(tab, "Attention", 1428, 420, 168, 42);
        AssertBox(tab, "Death Tests", 1624, 147, 252, 42);
        AssertBox(tab, "Attributes", 56, 483, 112, 21);
        AssertBox(tab, "Skills", 448, 483, 1148, 42);
        AssertBox(tab, "Equipment Proficiencies", 1624, 315, 644, 42);
        AssertBox(tab, "Quick Register", 56, 861, 1092, 42);
    }

    [Fact]
    public void DisplayEnrichmentUsesIndexedCoordinatesAndCannotMutateGeometry()
    {
        var sheet = new GoogleSheetsXlsxParser().Parse(LayoutWorkbook(), SheetSource("0"))!;
        var before = Geometry(sheet);
        var formatted = GoogleSheetsPublishedService.FormattedCsvValues("""
            The Renaissance of the Legends,,
            ,,
            22,,TRUE
            "Alysam, Hyoka",,
            """);
        Assert.Equal("22", formatted[(2, 0)]);
        Assert.Equal("TRUE", formatted[(2, 2)]);
        Assert.Equal("Alysam, Hyoka", formatted[(3, 0)]);
        var enriched = GoogleSheetsPublishedService.OverlayDisplayValues(sheet, formatted, sheet.Tabs[0].Id);
        Assert.Equal(before, Geometry(enriched));
        Assert.Equal("22", enriched.Tabs[0].Rows[2].Cells[0].DisplayValue);
        Assert.Equal("21.5", enriched.Tabs[0].Rows[2].Cells[0].RawValue);
        Assert.Equal("TRUE", enriched.Tabs[0].Rows[2].Cells[2].DisplayValue);
        Assert.Equal(sheet.Tabs[0].Rows[2].Cells[2].RawValue, enriched.Tabs[0].Rows[2].Cells[2].RawValue);
    }

    [Fact]
    public void FontsAndFloatingDrawingsAreMetadataOnly()
    {
        var sheet = new GoogleSheetsXlsxParser().Parse(LayoutWorkbook(), SheetSource("0"))!;
        var withoutImages = sheet with { Tabs = sheet.Tabs.Select(tab => tab with { Images = [] }).ToArray() };
        Assert.Equal(Geometry(withoutImages), Geometry(sheet));
        Assert.NotEmpty(sheet.Tabs[0].Images!);
        Assert.Equal("Arial", sheet.Tabs[0].Rows[0].Cells[0].FontFamily);
    }

    [Fact]
    public void SheetRendererUsesProviderSpecificFullWidthAndLocalOverflowWithoutChangingDocs()
    {
        var root = Root();
        var view = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedSheetView.razor"));
        var rowBatch = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedSheetRowBatch.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedSheetView.razor.css"));
        var cellCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedSheetRowBatch.razor.css"));
        var presentation = File.ReadAllText(Path.Combine(root, "Iridium.UI", "EmbeddedSheetCellPresentation.cs"));
        var preview = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageDocumentPreview.razor"));
        var previewCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageDocumentPreview.razor.css"));
        var embedsCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageDocumentEmbeds.razor.css"));
        var channelCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelView.razor.css"));
        Assert.Contains("<colgroup>", view);
        Assert.Contains("sheet-gridlines", view);
        Assert.Contains("sheet-no-gridlines", view);
        Assert.Contains("rowspan=\"@cell.Cell.RowSpan\" colspan=\"@cell.Cell.ColumnSpan\"", rowBatch);
        Assert.Contains("style=\"@row.HeightStyle\"", rowBatch);
        Assert.Contains("class=\"sheet-cell-content\"", rowBatch);
        Assert.Contains("class=\"sheet-checkbox-glyph\" role=\"checkbox\"", rowBatch);
        Assert.Contains("cell.Cell.DisplayValue", rowBatch);
        Assert.DoesNotContain("RawValue", rowBatch);
        Assert.Contains("--sheet-source-width", view);
        Assert.Contains("width:var(--sheet-source-width)", css);
        Assert.DoesNotContain("width:max(100%,var(--sheet-source-width))", css);
        Assert.Contains("overflow-wrap:normal", cellCss);
        Assert.Contains("word-break:normal", cellCss);
        Assert.DoesNotContain("word-break:break-all", cellCss);
        Assert.Contains("EmbeddedSheetFonts.Family", presentation);
        Assert.Contains("cell.IndentLevel * 10", presentation);
        Assert.Contains("sheet-floating-image", view);
        Assert.Equal("Arial, sans-serif", EmbeddedSheetFonts.Family("Arial"));
        Assert.Equal("Roboto, Arial, sans-serif", EmbeddedSheetFonts.Family("Roboto"));
        Assert.Equal("\"Lexend Deca\", var(--font-sans, Arial, sans-serif)",
            EmbeddedSheetFonts.Family("Lexend Deca"));
        Assert.Equal("\"Times New Roman\", serif", EmbeddedSheetFonts.Family("Times New Roman"));
        Assert.Equal("\"Courier New\", monospace", EmbeddedSheetFonts.Family("Courier New"));
        Assert.Null(EmbeddedSheetFonts.Family("url(https://evil.example/font)"));
        Assert.Contains("overflow-x:auto", css);
        Assert.Contains("max-width:none", css);
        Assert.Contains("padding:1px 3px", cellCss);
        Assert.Contains("padding:0 2px", cellCss);
        Assert.Contains(".sheet-checkbox,.sheet-numeric { padding-inline:1px; }", cellCss);
        Assert.Contains(".sheet-v-middle { vertical-align:middle; }", cellCss);
        Assert.Contains("CellHeight(_renderTab, cell)", view);
        Assert.Contains("table-layout:fixed", css);
        Assert.Contains("EmbeddedSheetCellPresentation.Style", view);
        Assert.Contains("sheet-preview", preview);
        Assert.Contains(".message-document-preview.sheet-preview{width:100%;max-width:none", previewCss);
        Assert.Contains("align-items:stretch", embedsCss);
        Assert.Contains(".embedded-sheet{width:100%;max-width:none", channelCss);
        Assert.Contains(".embedded-document{width:min(58rem,100%)", channelCss);
    }

    [Fact]
    public void SheetRendererEmitsExplicitAlignmentAndPerEdgeBorderStylesOnVisibleCells()
    {
        var centered = new EmbeddedSheetCellDto(2, 0, "22", RowSpan: 2, ColumnSpan: 3,
            HorizontalAlignment: EmbeddedDocumentTextAlignment.Center,
            VerticalAlignment: EmbeddedSheetVerticalAlignment.Middle, RightBorder: EmbeddedSheetBorderStyle.Medium,
            BottomBorder: EmbeddedSheetBorderStyle.Thick, RightBorderColor: "#112233",
            BottomBorderColor: "#445566", RawValue: "21.5");
        var centeredClass = EmbeddedSheetCellPresentation.CssClass(centered);
        var centeredStyle = EmbeddedSheetCellPresentation.Style(centered, 42);

        Assert.Contains("sheet-h-center", centeredClass);
        Assert.Contains("sheet-v-middle", centeredClass);
        Assert.Contains("sheet-numeric", centeredClass);
        Assert.Contains("text-align:center", centeredStyle);
        Assert.Contains("vertical-align:middle", centeredStyle);
        Assert.Contains("border-right:2px solid #112233", centeredStyle);
        Assert.Contains("border-bottom:3px solid #445566", centeredStyle);

        var right = centered with
        {
            HorizontalAlignment = EmbeddedDocumentTextAlignment.End,
            VerticalAlignment = EmbeddedSheetVerticalAlignment.Top,
            TopBorder = EmbeddedSheetBorderStyle.Double,
            TopBorderColor = "#778899",
            RightBorder = EmbeddedSheetBorderStyle.Dashed,
            RightBorderColor = null,
            BottomBorder = EmbeddedSheetBorderStyle.Dotted,
            BottomBorderColor = "#AABBCC"
        };
        var rightStyle = EmbeddedSheetCellPresentation.Style(right, 42);
        Assert.Contains("text-align:right", rightStyle);
        Assert.Contains("vertical-align:top", rightStyle);
        Assert.Contains("border-top:3px double #778899", rightStyle);
        Assert.Contains("border-right:1px dashed var(--text-muted, #9AA0A6)", rightStyle);
        Assert.Contains("border-bottom:1px dotted #AABBCC", rightStyle);

        var root = Root();
        var rowBatch = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedSheetRowBatch.razor"));
        Assert.Contains("class=\"@cell.CssClass\" style=\"@cell.Style\"", rowBatch);
        Assert.DoesNotContain("display: flex", File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components",
            "EmbeddedSheetRowBatch.razor.css")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedHostsAndRenderersDispatchSheetsWithoutDuplicatingGoogleDocsParser()
    {
        var root = Root();
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageDocumentEmbeds.razor"));
        var preview = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageDocumentPreview.razor"));
        var channel = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelView.razor"));
        var endpoints = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Api", "MessageDocumentEndpoints.cs"));
        Assert.Contains("FindSupportedContent(Content)", row);
        Assert.Contains("<EmbeddedSheetView", preview);
        Assert.Contains("<EmbeddedSheetView", channel);
        Assert.Contains("IEmbeddedContentService", endpoints);
        Assert.Contains("ParentForumChannelId ?? value.ChannelId", endpoints);
        Assert.Contains("CommunityPermission.ReadMessageHistory", endpoints);
        Assert.Contains("ParticipantAAccountId", endpoints);
        Assert.DoesNotContain("new GoogleDocsDocumentParser", endpoints);
    }

    [Fact]
    public void MessageImagesUseActualCanonicalDocumentIdAndMessageMediaHost()
    {
        var root = Root();
        var preview = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageDocumentPreview.razor"));
        var block = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedDocumentBlock.razor"));
        Assert.Contains("DocumentId=\"@Source.RequestIdentity\"", preview);
        var view = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedDocumentView.razor"));
        var documentBatch = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedDocumentBlockBatch.razor"));
        var sheetView = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmbeddedSheetView.razor"));
        Assert.Contains("DocumentId=\"@DocumentId\"", documentBatch);
        Assert.Contains("DocumentId=\"@DocumentId\"", view);
        Assert.Contains("DocumentId=\"@DocumentId\"", sheetView);
        Assert.DoesNotContain("DocumentId=\"DocumentId\"", view);
        Assert.DoesNotContain("DocumentId=\"DocumentId\"", documentBatch);
        Assert.DoesNotContain("DocumentId=\"DocumentId\"", sheetView);
        Assert.Contains("DocumentId=\"@DocumentId\"", block);
        Assert.DoesNotContain("DocumentId=\"DocumentId\"", block);
        Assert.Contains("DownloadCommunityMessageEmbedDocumentMediaAsync", block);
        Assert.Contains("DownloadDirectMessageEmbedDocumentMediaAsync", block);
        Assert.Contains("DownloadForumPostEmbedDocumentMediaAsync", block);
    }

    private static EmbeddedContentConfiguration SheetSource(string gid) => new(
        CommunityChannelEmbedProvider.GoogleSheets, SheetId,
        $"https://docs.google.com/spreadsheets/d/{SheetId}/view?gid={gid}",
        $"https://docs.google.com/spreadsheets/d/{SheetId}/gviz/tq?tqx=out:html&gid={gid}", gid);
    private static byte[] LayoutWorkbook()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Character" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Entry(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            Entry(archive, "xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>The Renaissance of the Legends</t></si><si><t>Alysam Hyoka</t></si></sst>
                """);
            Entry(archive, "xl/styles.xml", """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts><font><sz val="10"/><color rgb="FF000000"/></font><font><b/><sz val="22"/><name val="Arial"/><color rgb="FFFFFFFF"/></font></fonts>
                  <fills><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1155CC"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFC9DAF8"/></patternFill></fill></fills>
                  <numFmts count="1"><numFmt numFmtId="168" formatCode="0;-0"/></numFmts>
                  <borders><border/><border><bottom style="medium"><color rgb="FF073763"/></bottom></border><border><top style="double"><color rgb="FF1155CC"/></top></border><border><right style="thick"><color rgb="FF111111"/></right></border><border><top style="thin"><color rgb="FFCC0000"/></top><left style="thin"><color rgb="FF00AA00"/></left></border></borders>
                  <cellXfs><xf fontId="0" fillId="0" borderId="0" numFmtId="0"/><xf fontId="1" fillId="1" borderId="1" numFmtId="0"><alignment horizontal="center" vertical="center"/></xf><xf fontId="0" fillId="2" borderId="4" numFmtId="0"><alignment vertical="top"/></xf><xf fontId="1" fillId="2" borderId="0" numFmtId="0"/><xf fontId="0" fillId="0" borderId="2" numFmtId="0"><alignment indent="2"/></xf><xf fontId="0" fillId="0" borderId="0" numFmtId="168"><alignment horizontal="center" vertical="center"/></xf><xf fontId="0" fillId="0" borderId="3" numFmtId="0"/></cellXfs>
                </styleSheet>
                """);
            Entry(archive, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetPr/><sheetViews><sheetView showGridLines="0"/></sheetViews><sheetFormatPr defaultRowHeight="15" defaultColWidth="8.43"/><cols><col min="1" max="1" width="8"/><col min="2" max="2" width="30"/><col min="3" max="3" width="5"/></cols><sheetData>
                  <row r="1" ht="30"><c r="A1" s="1" t="s"><v>0</v></c><c r="C1" s="6"/></row>
                  <row r="2"><c r="C2" s="2"/></row>
                  <row r="3"><c r="A3" s="5"><f>1+1</f><v>21.5</v></c><c r="C3" t="b"><v>1</v></c></row>
                  <row r="4"><c r="A4" s="3" t="s"><v>1</v></c></row>
                  <row r="5" ht="6"><c r="A5" s="4"><v>42</v></c></row>
                </sheetData><mergeCells><mergeCell ref="A1:C1"/></mergeCells><drawing r:id="rDrawing" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"/></worksheet>
                """);
            Entry(archive, "xl/worksheets/_rels/sheet1.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rDrawing" Target="../drawings/drawing1.xml"/></Relationships>
                """);
            Entry(archive, "xl/drawings/drawing1.xml", """
                <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>95250</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>3</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>4</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="1" name="Appearance" descr="Character appearance"/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rImage"/></xdr:blipFill></xdr:pic></xdr:twoCellAnchor></xdr:wsDr>
                """);
            Entry(archive, "xl/drawings/_rels/drawing1.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rImage" Target="../media/image1.png"/></Relationships>
                """);
            Entry(archive, "xl/media/image1.png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        }
        return stream.ToArray();
    }
    private static byte[] RepresentativeRpGeometryWorkbook()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Personal Profile" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Entry(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            Entry(archive, "xl/styles.xml", """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts><font><sz val="10"/></font></fonts><fills><fill><patternFill patternType="none"/></fill></fills><borders><border/></borders><cellXfs><xf fontId="0" fillId="0" borderId="0" numFmtId="0"/></cellXfs></styleSheet>
                """);
            Entry(archive, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetFormatPr defaultRowHeight="15.75" defaultColWidth="3.25"/><cols><col min="1" max="113" width="3.25"/></cols><sheetData>
                  <row r="1"><c r="C1" t="inlineStr"><is><t>The Renaissance of the Legends</t></is></c></row>
                  <row r="5"><c r="I5" t="inlineStr"><is><t>Alysam Hyoka</t></is></c><c r="AJ5" t="inlineStr"><is><t>Player</t></is></c><c r="AW5" t="inlineStr"><is><t>Level</t></is></c></row>
                  <row r="6"><c r="AJ6" t="inlineStr"><is><t>Origin</t></is></c><c r="AW6" t="inlineStr"><is><t>Experience</t></is></c></row>
                  <row r="8"><c r="AQ8" t="inlineStr"><is><t>Evasion</t></is></c><c r="BG8" t="inlineStr"><is><t>Death Tests</t></is></c></row>
                  <row r="13"><c r="C13" t="inlineStr"><is><t>Vitality</t></is></c><c r="M13" t="inlineStr"><is><t>Mana</t></is></c><c r="W13" t="inlineStr"><is><t>Energy</t></is></c><c r="AG13" t="inlineStr"><is><t>Sanity</t></is></c><c r="AZ13" t="inlineStr"><is><t>Others</t></is></c></row>
                  <row r="16"><c r="AQ16" t="inlineStr"><is><t>Elusion</t></is></c><c r="BG16" t="inlineStr"><is><t>Equipment Proficiencies</t></is></c></row>
                  <row r="21"><c r="AZ21" t="inlineStr"><is><t>Attention</t></is></c></row>
                  <row r="24"><c r="C24" t="inlineStr"><is><t>Attributes</t></is></c><c r="Q24" t="inlineStr"><is><t>Skills</t></is></c></row>
                  <row r="42"><c r="C42" t="inlineStr"><is><t>Quick Register</t></is></c></row>
                </sheetData><mergeCells>
                  <mergeCell ref="C1:AF2"/><mergeCell ref="I5:AH6"/><mergeCell ref="AJ5:AL5"/><mergeCell ref="AJ6:AL6"/><mergeCell ref="AW5:AZ5"/><mergeCell ref="AW6:AZ6"/>
                  <mergeCell ref="C13:K14"/><mergeCell ref="M13:U14"/><mergeCell ref="W13:AE14"/><mergeCell ref="AG13:AO14"/><mergeCell ref="AQ8:AX9"/><mergeCell ref="AQ16:AX17"/>
                  <mergeCell ref="AZ13:BE14"/><mergeCell ref="AZ21:BE22"/><mergeCell ref="BG8:BO9"/><mergeCell ref="C24:F24"/><mergeCell ref="Q24:BE25"/><mergeCell ref="BG16:CC17"/><mergeCell ref="C42:AO43"/>
                </mergeCells></worksheet>
                """);
        }
        return stream.ToArray();
    }

    private static void AssertBox(EmbeddedSheetTabDto tab, string value, int x, int y, int width, int height)
    {
        var cell = Assert.Single(tab.Rows.SelectMany(row => row.Cells), cell => cell.DisplayValue == value);
        Assert.Equal(x, tab.ColumnWidths.Take(cell.Column).Sum());
        Assert.Equal(y, tab.Rows.Take(cell.Row).Sum(row => row.Height ?? 0));
        Assert.Equal(width, tab.ColumnWidths.Skip(cell.Column).Take(cell.ColumnSpan).Sum());
        Assert.Equal(height, tab.Rows.Skip(cell.Row).Take(cell.RowSpan).Sum(row => row.Height ?? 0));
    }
    private static void Entry(ZipArchive archive, string name, string source)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(source);
    }
    private static void Entry(ZipArchive archive, string name, byte[] source)
    {
        using var output = archive.CreateEntry(name).Open(); output.Write(source);
    }
    private static HttpResponseMessage Html(string source) => new(HttpStatusCode.OK)
        { Content = new StringContent(source) { Headers = { ContentType = new("text/html") } } };
    private static HttpResponseMessage Csv(string source) => new(HttpStatusCode.OK)
        { Content = new StringContent(source) { Headers = { ContentType = new("text/csv") } } };
    private static string Geometry(EmbeddedSheetDto sheet) => string.Join('|', sheet.Tabs.Select(tab =>
        $"{tab.Id}:{string.Join(',', tab.ColumnWidths)}:{string.Join(';', tab.Rows.Select(row =>
            $"{row.Index},{row.Height}:{string.Join(',', row.Cells.Select(cell => $"{cell.Row}/{cell.Column}/{cell.RowSpan}/{cell.ColumnSpan}"))}"))}"));
    private static GoogleSheetsPublishedService Service(HttpClient http, IMemoryCache cache,
        GoogleDocsImportSettings? settings = null) => new(
        new Factory(http), cache, new GoogleSheetsHtmlParser(), new GoogleSheetsXlsxParser(),
        settings ?? GoogleDocsImportSettings.Default,
        new Lifetime(), NullLogger<GoogleSheetsPublishedService>.Instance);
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class SequenceHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public SequenceHandler(IEnumerable<HttpResponseMessage> responses) : this(new(responses)) { }
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { RequestCount++; return Task.FromResult(responses.Dequeue()); }
    }
    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => response(cancellationToken);
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private static string Root([CallerFilePath] string sourceFile = "")
    {
        if (File.Exists(sourceFile) && Directory.GetParent(sourceFile)?.Parent is { } projectRoot &&
            File.Exists(Path.Combine(projectRoot.FullName, "Iridium.sln"))) return projectRoot.FullName;
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
