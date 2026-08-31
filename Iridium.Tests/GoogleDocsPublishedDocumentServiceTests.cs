using System.Net;
using System.Text.Json;
using Iridium.Protocol;
using Iridium.Server.Embeds;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iridium.Tests;

public sealed class GoogleDocsPublishedDocumentServiceTests
{
    private const string PublishedUrl = "https://docs.google.com/document/d/e/2PACX-abcdefghij_123/pub";

    [Fact]
    public void ParserCreatesSemanticBlocksAndComposesSupportedInlineFormatting()
    {
        var result = Parse("""
            <html><head><style>.bold{font-weight:700}.italic{font-style:italic}.under{text-decoration:underline}.red{color:#d50000}.center{text-align:center}</style></head>
            <body><h2>Introduction</h2><p class="center"><span class="bold italic under red">Formatted</span> text<br>line</p>
            <ul><li>One<ol><li>Nested</li></ol></li></ul><table><tr><th>Name</th><td>Skye</td></tr></table><hr></body></html>
            """);
        Assert.Collection(result.Document.Blocks,
            block => Assert.IsType<EmbeddedDocumentHeadingDto>(block),
            block =>
            {
                var paragraph = Assert.IsType<EmbeddedDocumentParagraphDto>(block);
                Assert.Equal(EmbeddedDocumentTextAlignment.Center, paragraph.Alignment);
                var text = Assert.IsType<EmbeddedDocumentTextDto>(paragraph.Content[0]);
                Assert.True(text.Bold && text.Italic && text.Underline);
                Assert.Equal("Formatted", text.Text);
                Assert.Equal(EmbeddedDocumentTextColor.Red, text.TextColor);
                Assert.Contains(paragraph.Content, value => value is EmbeddedDocumentLineBreakDto);
            },
            block => Assert.IsType<EmbeddedDocumentListDto>(block),
            block => Assert.IsType<EmbeddedDocumentTableDto>(block),
            block => Assert.IsType<EmbeddedDocumentHorizontalRuleDto>(block));
    }

    [Fact]
    public void ParserDropsActiveContentAndUnsafeLinksButFlattensUnknownMarkup()
    {
        var result = Parse("""
            <body><script>bad</script><iframe src="https://evil.example"></iframe><object>bad</object><form>bad</form>
            <p onclick="bad()"><a href="javascript:bad()">Unsafe text</a> <a href="https://example.com/safe">Safe</a>
            <span style="background:url(javascript:bad());color:red">kept text</span></p></body>
            """);
        var paragraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(result.Document.Blocks));
        Assert.Contains(paragraph.Content, value => value is EmbeddedDocumentTextDto { Text: "Unsafe text" });
        Assert.Contains(paragraph.Content, value => value is EmbeddedDocumentLinkDto { Url: "https://example.com/safe" });
        Assert.DoesNotContain(paragraph.Content, value => value is EmbeddedDocumentLinkDto { Url: var url } &&
            url.StartsWith("javascript", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(EmbeddedDocumentTextColor.Red,
            Assert.Single(paragraph.Content.OfType<EmbeddedDocumentTextDto>(), value => value.Text == "kept text").TextColor);
    }

    [Theory]
    [InlineData("#000000", EmbeddedDocumentTextColor.Default)]
    [InlineData("#444444", EmbeddedDocumentTextColor.Default)]
    [InlineData("#ffffff", EmbeddedDocumentTextColor.Default)]
    [InlineData("#d50000", EmbeddedDocumentTextColor.Red)]
    [InlineData("#ff9900", EmbeddedDocumentTextColor.Orange)]
    [InlineData("#ffff00", EmbeddedDocumentTextColor.Yellow)]
    [InlineData("#00aa00", EmbeddedDocumentTextColor.Green)]
    [InlineData("#00aaaa", EmbeddedDocumentTextColor.Teal)]
    [InlineData("#1155cc", EmbeddedDocumentTextColor.Blue)]
    [InlineData("#9900ff", EmbeddedDocumentTextColor.Purple)]
    [InlineData("#ff00aa", EmbeddedDocumentTextColor.Pink)]
    [InlineData("#999999", EmbeddedDocumentTextColor.Gray)]
    [InlineData("#f00", EmbeddedDocumentTextColor.Red)]
    [InlineData("rgb(17, 85, 204)", EmbeddedDocumentTextColor.Blue)]
    [InlineData("rgba(255, 0, 0, 0.8)", EmbeddedDocumentTextColor.Red)]
    [InlineData("rgba(255, 0, 0, 0)", EmbeddedDocumentTextColor.Default)]
    [InlineData("var(--google-red)", EmbeddedDocumentTextColor.Default)]
    [InlineData("not-a-color", EmbeddedDocumentTextColor.Default)]
    public void ParserMapsSupportedSourceColorsToCuratedPalette(string source,
        EmbeddedDocumentTextColor expected)
    {
        var result = Parse($"<body><p><span style='color:{source}'>Colored</span></p></body>");
        var paragraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(result.Document.Blocks));
        Assert.Equal(expected, Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(paragraph.Content)).TextColor);
    }

    [Fact]
    public void ColorComposesAcrossFormattingLinksHeadingsListsAndTables()
    {
        var result = Parse("""
            <html><head><style>
            .red{color:#d50000;font-weight:700}.blue{color:rgb(17,85,204);font-style:italic}
            .purple{color:#9900ff;text-decoration:underline}.green{color:#00aa00}
            </style></head><body>
            <h2 class="red">Heading</h2>
            <p><span class="red">Bold</span> <span class="blue">Italic</span>
            <span class="purple">Underline</span> <a class="green" href="https://example.com">Link</a></p>
            <ul><li><span class="blue">List</span></li></ul>
            <table><tr><td><span class="purple">Cell</span></td></tr></table>
            </body></html>
            """);
        var heading = Assert.IsType<EmbeddedDocumentHeadingDto>(result.Document.Blocks[0]);
        Assert.Equal(EmbeddedDocumentTextColor.Red,
            Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(heading.Content)).TextColor);
        Assert.True(Assert.IsType<EmbeddedDocumentTextDto>(heading.Content[0]).Bold);

        var paragraph = Assert.IsType<EmbeddedDocumentParagraphDto>(result.Document.Blocks[1]);
        var text = paragraph.Content.OfType<EmbeddedDocumentTextDto>().ToList();
        Assert.Contains(text, value => value.Text == "Bold" && value.Bold && value.TextColor == EmbeddedDocumentTextColor.Red);
        Assert.Contains(text, value => value.Text == "Italic" && value.Italic && value.TextColor == EmbeddedDocumentTextColor.Blue);
        Assert.Contains(text, value => value.Text == "Underline" && value.Underline && value.TextColor == EmbeddedDocumentTextColor.Purple);
        var link = Assert.Single(paragraph.Content.OfType<EmbeddedDocumentLinkDto>());
        Assert.Equal(EmbeddedDocumentTextColor.Green,
            Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(link.Content)).TextColor);

        var list = Assert.IsType<EmbeddedDocumentListDto>(result.Document.Blocks[2]);
        var listParagraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(Assert.Single(list.Items).Blocks));
        Assert.Equal(EmbeddedDocumentTextColor.Blue,
            Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(listParagraph.Content)).TextColor);
        var table = Assert.IsType<EmbeddedDocumentTableDto>(result.Document.Blocks[3]);
        var cellParagraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(table.Rows[0].Cells[0].Blocks));
        Assert.Equal(EmbeddedDocumentTextColor.Purple,
            Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(cellParagraph.Content)).TextColor);
    }

    [Fact]
    public void ParserCreatesOpaqueMediaReferencesOnlyForApprovedGoogleImages()
    {
        var result = Parse("<body><img src='https://evil.example/tracker.png'><img src='https://lh4.googleusercontent.com/art.png' alt='Art'></body>");
        var image = Assert.IsType<EmbeddedDocumentImageDto>(Assert.Single(result.Document.Blocks));
        Assert.Equal("Art", image.Alt);
        Assert.DoesNotContain("google", image.MediaId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("lh4.googleusercontent.com", Assert.Single(result.Media).Value.Source?.Host);
    }

    [Fact]
    public void ParserReconstructsExportedInlineImagesWithDimensionsAndAlignment()
    {
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
        var encoded = Convert.ToBase64String(png);
        var result = Parse($"""
            <body><p style="text-align:right"><span><img alt="Character" src="data:image/png;base64,{encoded}"
            style="width: 194.4px; height: 244.5px; margin-left:0"></span></p><p>After image</p></body>
            """);
        var image = Assert.IsType<EmbeddedDocumentImageDto>(result.Document.Blocks[0]);
        Assert.Equal(194, image.Width);
        Assert.Equal(245, image.Height);
        Assert.Equal(EmbeddedDocumentTextAlignment.End, image.Alignment);
        var reference = Assert.Single(result.Media).Value;
        Assert.Equal(png, reference.InlineBytes);
        Assert.Null(reference.Source);
        Assert.IsType<EmbeddedDocumentParagraphDto>(result.Document.Blocks[1]);
    }

    [Fact]
    public void ParserCollapsesEmptyBlocksAndRepeatedBreakArtifacts()
    {
        var result = Parse("<body><div>   </div><p><br><br><br>First<br><br><br><br>Second<br></p><p>Stat: 8</p><p>Cost: 3</p></body>");
        Assert.Equal(3, result.Document.Blocks.Count);
        var first = Assert.IsType<EmbeddedDocumentParagraphDto>(result.Document.Blocks[0]);
        Assert.IsType<EmbeddedDocumentTextDto>(first.Content[0]);
        Assert.Equal(2, first.Content.Count(value => value is EmbeddedDocumentLineBreakDto));
        Assert.IsType<EmbeddedDocumentParagraphDto>(result.Document.Blocks[1]);
        Assert.IsType<EmbeddedDocumentParagraphDto>(result.Document.Blocks[2]);
    }

    [Fact]
    public void ParagraphFlowBreaksAndIntentionalBlankParagraphsRemainDistinct()
    {
        var wrapped = Parse("<body><p>Born of royal Daemon blood, Aziel and his sister\nwere brought to Lethraim.</p></body>");
        var wrappedParagraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(wrapped.Document.Blocks));
        Assert.DoesNotContain(wrappedParagraph.Content, value => value is EmbeddedDocumentLineBreakDto);
        Assert.Contains("were brought", Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(wrappedParagraph.Content)).Text);

        var adjacent = Parse("<body><p>Attack Strength: 8</p><p>SP Cost: 3 SP</p></body>");
        Assert.Collection(adjacent.Document.Blocks,
            block => Assert.IsType<EmbeddedDocumentParagraphDto>(block),
            block => Assert.IsType<EmbeddedDocumentParagraphDto>(block));

        var blank = Parse("<body><p>Paragraph A</p><p><span></span></p><p>Paragraph B</p></body>");
        Assert.Collection(blank.Document.Blocks,
            block => Assert.IsType<EmbeddedDocumentParagraphDto>(block),
            block => Assert.IsType<EmbeddedDocumentSpacerDto>(block),
            block => Assert.IsType<EmbeddedDocumentParagraphDto>(block));

        var twoBlanks = Parse("<body><p>A</p><p></p><p><br></p><p>B</p></body>");
        Assert.Equal(2, twoBlanks.Document.Blocks.Count(value => value is EmbeddedDocumentSpacerDto));
    }

    [Fact]
    public void ExplicitBreaksStayInlineAndEmptyWrapperSpansAreIgnored()
    {
        var oneBreak = Parse("<body><div><span>   </span></div><p>Line A<br>Line B</p></body>");
        var paragraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(oneBreak.Document.Blocks));
        Assert.Equal(1, paragraph.Content.Count(value => value is EmbeddedDocumentLineBreakDto));

        var blankLine = Parse("<body><p>Line A<br><br>Line B</p></body>");
        var blankLineParagraph = Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(blankLine.Document.Blocks));
        Assert.Equal(2, blankLineParagraph.Content.Count(value => value is EmbeddedDocumentLineBreakDto));
        Assert.DoesNotContain(blankLine.Document.Blocks, value => value is EmbeddedDocumentSpacerDto);
    }

    [Fact]
    public void InvalidInlineImageIsDroppedWithoutFailingRemainingDocument()
    {
        var result = Parse("<body><p><img src='data:image/png;base64,not-base64'></p><p>Still here</p></body>");
        Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(result.Document.Blocks));
        Assert.Empty(result.Media);
    }

    [Fact]
    public async Task InlineExportImageUsesValidatedMediaCacheWithoutRemoteFetch()
    {
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
        var handler = new StubHandler(_ => Html($"<body><img src='data:image/png;base64,{Convert.ToBase64String(png)}'></body>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var document = await service.GetAsync(PublishedConfiguration());
            var id = Assert.IsType<EmbeddedDocumentImageDto>(Assert.Single(document.Document!.Blocks)).MediaId;
            var media = await service.GetMediaAsync(PublishedConfiguration(), id);
            Assert.Equal(png, media!.Bytes);
            Assert.Equal("image/png", media.ContentType);
            Assert.Equal(1, handler.RequestCount);
        }
    }

    [Fact]
    public void StructuredDocumentRoundTripsAcrossProtocolJsonBoundary()
    {
        var original = new ChannelEmbedDocumentDto(ChannelEmbedDocumentStatus.Ready,
            new([new EmbeddedDocumentHeadingDto(1, [new EmbeddedDocumentTextDto("Title", Bold: true,
                     TextColor: EmbeddedDocumentTextColor.Purple)]),
                 new EmbeddedDocumentSpacerDto(),
                 new EmbeddedDocumentImageDto("0123456789abcdef0123456789abcdef", "Artwork")]),
            DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(original, JsonSerializerOptions.Web);
        Assert.DoesNotContain("<h1", json, StringComparison.OrdinalIgnoreCase);
        var copy = JsonSerializer.Deserialize<ChannelEmbedDocumentDto>(json, JsonSerializerOptions.Web);
        var heading = Assert.IsType<EmbeddedDocumentHeadingDto>(copy!.Document!.Blocks[0]);
        Assert.Equal(EmbeddedDocumentTextColor.Purple,
            Assert.IsType<EmbeddedDocumentTextDto>(Assert.Single(heading.Content)).TextColor);
        Assert.IsType<EmbeddedDocumentSpacerDto>(copy.Document.Blocks[1]);
        Assert.IsType<EmbeddedDocumentImageDto>(copy.Document.Blocks[2]);
    }

    [Fact]
    public async Task MediaIsFetchedThroughBoundedValidatedCache()
    {
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
        var handler = new StubHandler(request => request.RequestUri!.Host == "docs.google.com"
            ? Html("<body><img src='https://lh4.googleusercontent.com/art.png'></body>")
            : Image(png, "image/png"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var document = await service.GetAsync(PublishedConfiguration());
            var id = Assert.IsType<EmbeddedDocumentImageDto>(Assert.Single(document.Document!.Blocks)).MediaId;
            var first = await service.GetMediaAsync(PublishedConfiguration(), id);
            var second = await service.GetMediaAsync(PublishedConfiguration(), id);
            Assert.Equal(png, first!.Bytes);
            Assert.Same(first, second);
            Assert.Equal(2, handler.RequestCount);
        }
    }

    [Fact]
    public async Task PublicPublishedDocumentIsFetchedParsedAndCached()
    {
        var handler = new StubHandler(_ => Html("<html><body><h2>Hello</h2><script>bad()</script></body></html>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var first = await service.GetAsync(PublishedConfiguration());
            var second = await service.GetAsync(PublishedConfiguration());
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, first.Status);
            Assert.IsType<EmbeddedDocumentHeadingDto>(Assert.Single(first.Document!.Blocks));
            Assert.Equal(first, second);
            Assert.False(string.IsNullOrWhiteSpace(first.ContentVersion));
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(PublishedUrl, handler.LastRequestUri?.AbsoluteUri);
        }
    }

    [Fact]
    public async Task SameDocumentIdentityFromChannelForumAndMessageHostsUsesOneProviderCacheEntry()
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleDocs(
            "https://docs.google.com/document/d/abc_DEF-123456/edit?usp=sharing", out var channelSource));
        Assert.True(CommunityChannelEmbeds.TryGoogleDocs(
            "https://docs.google.com/document/d/abc_DEF-123456/view", out var forumPostSource));
        var messageSource = Assert.Single(CommunityChannelEmbeds.FindGoogleDocs(
            "https://docs.google.com/document/d/abc_DEF-123456/edit?usp=sharing"));
        var handler = new StubHandler(_ => Html("<html><body><p>Shared host document</p></body></html>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var channelDocument = await service.GetAsync(channelSource!);
            var forumPostDocument = await service.GetAsync(forumPostSource!);
            var messageDocument = await service.GetAsync(messageSource);
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, channelDocument.Status);
            Assert.Same(channelDocument, forumPostDocument);
            Assert.Same(channelDocument, messageDocument);
            Assert.Equal(channelDocument.ContentVersion, messageDocument.ContentVersion);
            Assert.Equal(1, handler.RequestCount);
        }
    }

    [Fact]
    public async Task ConcurrentHostsShareOneInFlightProviderImport()
    {
        var handler = new DelayedStubHandler(Html("<html><body><p>Shared in flight</p></body></html>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var channel = service.GetAsync(SharedConfiguration());
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var message = service.GetAsync(SharedConfiguration());
            handler.Release.TrySetResult();
            var results = await Task.WhenAll(channel, message);
            Assert.All(results, result => Assert.Equal(ChannelEmbedDocumentStatus.Ready, result.Status));
            Assert.Equal(1, handler.RequestCount);
        }
    }

    [Fact]
    public async Task SlowSourceBelowConfiguredTimeoutSucceeds()
    {
        var handler = new AsyncStubHandler(async cancellationToken =>
        {
            await Task.Delay(40, cancellationToken);
            return Html("<body><p>Slow but valid</p></body>");
        });
        var service = Create(handler, out var cache, out var http, sourceTimeout: TimeSpan.FromSeconds(1));
        using (http) using (cache)
            Assert.Equal(ChannelEmbedDocumentStatus.Ready,
                (await service.GetAsync(SharedConfiguration())).Status);
    }

    [Fact]
    public async Task SourcePastConfiguredTimeoutReturnsExplicitTimeout()
    {
        var handler = new AsyncStubHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Html("<body><p>Never reached</p></body>");
        });
        var service = Create(handler, out var cache, out var http, sourceTimeout: TimeSpan.FromMilliseconds(40));
        using (http) using (cache)
            Assert.Equal(ChannelEmbedDocumentStatus.Timeout,
                (await service.GetAsync(SharedConfiguration())).Status);
    }

    [Fact]
    public async Task CallerCancellationStopsWaitingButSharedImportWarmsCache()
    {
        var handler = new DelayedStubHandler(Html("<body><p>Cache survives caller</p></body>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            using var caller = new CancellationTokenSource();
            var abandoned = service.GetAsync(SharedConfiguration(), caller.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

            var waitingViewer = service.GetAsync(SharedConfiguration());
            handler.Release.TrySetResult();
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, (await waitingViewer).Status);
            Assert.Equal(ChannelEmbedDocumentStatus.Ready,
                (await service.GetAsync(SharedConfiguration())).Status);
            Assert.Equal(1, handler.RequestCount);
        }
    }

    [Fact]
    public async Task ApplicationShutdownCancelsSharedImport()
    {
        var handler = new AsyncStubHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Html("<body><p>Never reached</p></body>");
        });
        var lifetime = new TestHostApplicationLifetime();
        var service = Create(handler, out var cache, out var http, lifetime: lifetime);
        using (http) using (cache)
        {
            var import = service.GetAsync(SharedConfiguration());
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lifetime.StopApplication();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        }
    }

    [Fact]
    public async Task ExpiredFreshEntryKeepsLastGoodDocumentWhenRefreshFails()
    {
        var handler = new SequenceStubHandler([
            () => Html("<body><p>Last good</p></body>"),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        ]);
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var first = await service.GetAsync(SharedConfiguration());
            cache.Remove("google-doc:AnonymousExport:abc_DEF-123456");
            var stale = await service.GetAsync(SharedConfiguration());
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, stale.Status);
            Assert.True(stale.IsStale);
            Assert.Equal(first.Document, stale.Document);
            Assert.Equal(2, handler.RequestCount);
        }
    }

    [Fact]
    public async Task ManualRefreshForcesRevalidationAndChangedContentGetsNewVersion()
    {
        var handler = new SequenceStubHandler([
            () => Html("<body><p>Version one</p></body>"),
            () => Html("<body><p>Version two</p></body>")
        ]);
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var first = await service.GetAsync(SharedConfiguration());
            var refreshed = await service.RefreshAsync(SharedConfiguration());
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, refreshed.Status);
            Assert.NotEqual(first.ContentVersion, refreshed.ContentVersion);
            Assert.Contains("Version two", JsonSerializer.Serialize(refreshed.Document));
            Assert.Equal(2, handler.RequestCount);
        }
    }

    [Fact]
    public async Task UnchangedRefreshRenewsFreshnessWithoutChangingContentVersion()
    {
        const string html = "<body><p>Unchanged</p></body>";
        var handler = new SequenceStubHandler([() => Html(html), () => Html(html)]);
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var first = await service.GetAsync(SharedConfiguration());
            var refreshed = await service.RefreshAsync(SharedConfiguration());
            Assert.Equal(first.ContentVersion, refreshed.ContentVersion);
            Assert.Equal(first.Document, refreshed.Document);
            Assert.False(refreshed.IsStale);
            Assert.Equal(2, handler.RequestCount);
        }
    }

    [Fact]
    public async Task ConcurrentStaleReadersReturnLastGoodAndLaunchOneRefresh()
    {
        var handler = new RevalidatingStubHandler();
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var first = await service.GetAsync(SharedConfiguration());
            cache.Remove("google-doc:AnonymousExport:abc_DEF-123456");
            var staleResults = await Task.WhenAll(Enumerable.Range(0, 10)
                .Select(_ => service.GetAsync(SharedConfiguration())));
            Assert.All(staleResults, value => Assert.True(value.IsStale));
            await handler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, handler.RequestCount);
            handler.ReleaseRefresh.TrySetResult();
            ChannelEmbedDocumentDto? fresh = null;
            for (var attempt = 0; attempt < 100 && fresh is null; attempt++)
            {
                cache.TryGetValue("google-doc:AnonymousExport:abc_DEF-123456", out fresh);
                if (fresh is null) await Task.Delay(10);
            }
            Assert.NotNull(fresh);
            Assert.False(fresh.IsStale);
            Assert.NotEqual(first.ContentVersion, fresh.ContentVersion);
            Assert.Equal(2, handler.RequestCount);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task RealisticLargeTextExportsRemainImportable(int megabytes)
    {
        var payload = $"<html><body><p>{new string('x', megabytes * 1024 * 1024)}</p></body></html>";
        var handler = new StubHandler(_ => Html(payload));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var result = await service.GetAsync(PublishedConfiguration());
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, result.Status);
            Assert.IsType<EmbeddedDocumentParagraphDto>(Assert.Single(result.Document!.Blocks));
        }
    }

    [Fact]
    public void ThousandsOfParagraphsAndSpansRemainWithinComplexityLimits()
    {
        var source = new System.Text.StringBuilder("<body>");
        for (var index = 0; index < 4_000; index++)
            source.Append("<p><span>First</span><span>Second</span><span>Third</span></p>");
        source.Append("</body>");
        var parsed = Parse(source.ToString());
        Assert.Equal(4_000, parsed.Metrics.Blocks);
        Assert.Equal(12_000, parsed.Metrics.Spans);
        Assert.Equal(4_000, parsed.Document.Blocks.Count);
    }

    [Fact]
    public void ManyImagesAreNormalizedWithoutBlockingDocumentText()
    {
        var source = new System.Text.StringBuilder("<body><p>Document text</p>");
        for (var index = 0; index < 250; index++)
            source.Append($"<img src='https://lh4.googleusercontent.com/image-{index}.png'>");
        source.Append("</body>");
        var parsed = Parse(source.ToString());
        Assert.Equal(250, parsed.Metrics.Images);
        Assert.Equal(251, parsed.Metrics.Blocks);
        Assert.IsType<EmbeddedDocumentParagraphDto>(parsed.Document.Blocks[0]);
    }

    [Fact]
    public async Task OversizedImageFailsIndependentlyAfterDocumentSucceeds()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "docs.google.com"
            ? Html("<body><p>Text survives</p><img src='https://lh4.googleusercontent.com/oversized.png'></body>")
            : OversizedMediaResponse());
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var document = await service.GetAsync(PublishedConfiguration());
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, document.Status);
            Assert.IsType<EmbeddedDocumentParagraphDto>(document.Document!.Blocks[0]);
            var image = Assert.IsType<EmbeddedDocumentImageDto>(document.Document.Blocks[1]);
            Assert.Null(await service.GetMediaAsync(PublishedConfiguration(), image.MediaId));
        }
    }

    [Fact]
    public async Task UnparseableHtmlHasDedicatedResult()
    {
        var handler = new StubHandler(_ => Html("<html><body><script>nothing usable</script></body></html>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
            Assert.Equal(ChannelEmbedDocumentStatus.ParseFailure,
                (await service.GetAsync(PublishedConfiguration())).Status);
    }

    [Fact]
    public async Task AnyoneWithLinkDocumentUsesCanonicalAnonymousHtmlExport()
    {
        Assert.True(CommunityChannelEmbeds.TryGoogleDocs(
            "https://docs.google.com/document/d/abc_DEF-123456/edit?usp=sharing", out var configuration));
        var handler = new StubHandler(_ => Html("<html><body><h1>Shared document</h1></body></html>"));
        var service = Create(handler, out var cache, out var http);
        using (http) using (cache)
        {
            var result = await service.GetAsync(configuration!);
            Assert.Equal(ChannelEmbedDocumentStatus.Ready, result.Status);
            Assert.IsType<EmbeddedDocumentHeadingDto>(Assert.Single(result.Document!.Blocks));
            Assert.Equal("https://docs.google.com/document/d/abc_DEF-123456/export?format=html",
                handler.LastRequestUri?.AbsoluteUri);
        }
    }

    [Fact]
    public async Task LoginOrAccessHtmlIsNotParsedAsDocumentContent()
    {
        foreach (var html in new[]
                 {
                     "<html><head><title>Sign in - Google Accounts</title></head><body><h1>Sign in</h1></body></html>",
                     "<html><body><form action='https://accounts.google.com/ServiceLogin'><p>Continue</p></form></body></html>",
                     "<html><head><title>You need access</title></head><body><p>Request access</p></body></html>"
                 })
        {
            var handler = new StubHandler(_ => Html(html));
            var service = Create(handler, out var cache, out var http);
            using (http) using (cache)
            {
                var result = await service.GetAsync(SharedConfiguration());
                Assert.Equal(ChannelEmbedDocumentStatus.AuthenticationRequired, result.Status);
                Assert.Null(result.Document);
            }
        }
    }

    [Fact]
    public async Task HttpFailuresAreClassifiedAndFailClosed()
    {
        foreach (var (response, expected) in new (Func<HttpResponseMessage>, ChannelEmbedDocumentStatus)[]
                 {
                     (() => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.example") } },
                         ChannelEmbedDocumentStatus.Unsupported),
                     (() => new(HttpStatusCode.Forbidden), ChannelEmbedDocumentStatus.AuthenticationRequired),
                     (() => new(HttpStatusCode.NotFound), ChannelEmbedDocumentStatus.NotFound),
                     (OversizedResponse, ChannelEmbedDocumentStatus.TooLarge)
                 })
        {
            var handler = new StubHandler(_ => response());
            var service = Create(handler, out var cache, out var http);
            using (http) using (cache)
            {
                var result = await service.GetAsync(PublishedConfiguration());
                Assert.Equal(expected, result.Status);
                Assert.Null(result.Document);
            }
        }
        var unexpectedCancellation = new StubHandler(_ => throw new TaskCanceledException("transport aborted"));
        var cancellationService = Create(unexpectedCancellation, out var cancellationCache,
            out var cancellationHttp);
        using (cancellationHttp) using (cancellationCache)
            Assert.Equal(ChannelEmbedDocumentStatus.TemporaryFailure,
                (await cancellationService.GetAsync(PublishedConfiguration())).Status);
    }

    private static GoogleDocsParseResult Parse(string source) => new GoogleDocsDocumentParser().Parse(source,
        "2PACX-abcdefghij_123", new(PublishedUrl))!;
    private static GoogleDocsEmbedConfiguration PublishedConfiguration() =>
        new("2PACX-abcdefghij_123", PublishedUrl, $"{PublishedUrl}?embedded=true", PublishedUrl,
            InputKind: GoogleDocsInputKind.PublishedLink);
    private static GoogleDocsEmbedConfiguration SharedConfiguration() =>
        new("abc_DEF-123456", "https://docs.google.com/document/d/abc_DEF-123456/view",
            "https://docs.google.com/document/d/abc_DEF-123456/preview",
            AnonymousExportUrl: "https://docs.google.com/document/d/abc_DEF-123456/export?format=html");
    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value) { Headers = { ContentType = new("text/html") } } };
    private static HttpResponseMessage Image(byte[] value, string contentType) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(value) { Headers = { ContentType = new(contentType) } } };
    private static GoogleDocsPublishedDocumentService Create(HttpMessageHandler handler, out MemoryCache cache,
        out HttpClient http, TimeSpan? sourceTimeout = null, TestHostApplicationLifetime? lifetime = null)
    {
        cache = new(new MemoryCacheOptions());
        http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new(new SingleHttpClientFactory(http), cache, TimeProvider.System, new GoogleDocsDocumentParser(),
            NullLogger<GoogleDocsPublishedDocumentService>.Instance, lifetime ?? new(),
            new(sourceTimeout ?? GoogleDocsPublishedDocumentService.SourceFetchTimeout,
                GoogleDocsPublishedDocumentService.MediaFetchTimeout));
    }
    private static HttpResponseMessage OversizedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = GoogleDocsPublishedDocumentService.MaximumResponseBytes + 1L;
        response.Content.Headers.ContentType = new("text/html"); return response;
    }
    private static HttpResponseMessage OversizedMediaResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = GoogleDocsPublishedDocumentService.MaximumMediaBytes + 1L;
        response.Content.Headers.ContentType = new("image/png"); return response;
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { RequestCount++; LastRequestUri = request.RequestUri; return Task.FromResult(response(request)); }
    }
    private sealed class DelayedStubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return response;
        }
    }
    private sealed class AsyncStubHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Started.TrySetResult();
            return response(cancellationToken);
        }
    }
    private sealed class SequenceStubHandler(Queue<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        public SequenceStubHandler(IEnumerable<Func<HttpResponseMessage>> responses) : this(new(responses)) { }
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responses.Dequeue()());
        }
    }
    private sealed class RevalidatingStubHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1) return Html("<body><p>Last good</p></body>");
            RefreshStarted.TrySetResult();
            await ReleaseRefresh.Task.WaitAsync(cancellationToken);
            return Html("<body><p>Refreshed</p></body>");
        }
    }
    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }
}
