using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iridium.Protocol;
using Microsoft.Extensions.Caching.Memory;

namespace Iridium.Server.Embeds;

public interface IEmbeddedContentService
{
    Task<ChannelEmbedDocumentDto> GetAsync(EmbeddedContentConfiguration source, CancellationToken cancellationToken = default);
    Task<ChannelEmbedDocumentDto> RefreshAsync(EmbeddedContentConfiguration source, CancellationToken cancellationToken = default);
    Task<EmbeddedDocumentMedia?> GetMediaAsync(EmbeddedContentConfiguration source, string mediaId,
        CancellationToken cancellationToken = default);
}

public sealed class EmbeddedContentService(IGoogleDocsPublishedDocumentService documents,
    GoogleSheetsPublishedService sheets) : IEmbeddedContentService
{
    public Task<ChannelEmbedDocumentDto> GetAsync(EmbeddedContentConfiguration source,
        CancellationToken cancellationToken = default) => source.Provider switch
    {
        CommunityChannelEmbedProvider.GoogleDocs when CommunityChannelEmbeds.TryGoogleDocs(source.OpenUrl, out var value) =>
            documents.GetAsync(value!, cancellationToken),
        CommunityChannelEmbedProvider.GoogleSheets => sheets.GetAsync(source, cancellationToken),
        _ => Task.FromResult(new ChannelEmbedDocumentDto(ChannelEmbedDocumentStatus.Unsupported, null))
    };

    public Task<ChannelEmbedDocumentDto> RefreshAsync(EmbeddedContentConfiguration source,
        CancellationToken cancellationToken = default) => source.Provider switch
    {
        CommunityChannelEmbedProvider.GoogleDocs when CommunityChannelEmbeds.TryGoogleDocs(source.OpenUrl, out var value) =>
            documents.RefreshAsync(value!, cancellationToken),
        CommunityChannelEmbedProvider.GoogleSheets => sheets.RefreshAsync(source, cancellationToken),
        _ => Task.FromResult(new ChannelEmbedDocumentDto(ChannelEmbedDocumentStatus.Unsupported, null))
    };

    public Task<EmbeddedDocumentMedia?> GetMediaAsync(EmbeddedContentConfiguration source, string mediaId,
        CancellationToken cancellationToken = default) => source.Provider switch
    {
        CommunityChannelEmbedProvider.GoogleDocs when CommunityChannelEmbeds.TryGoogleDocs(source.OpenUrl, out var value) =>
            documents.GetMediaAsync(value!, mediaId, cancellationToken),
        CommunityChannelEmbedProvider.GoogleSheets => sheets.GetMediaAsync(source, mediaId, cancellationToken),
        _ => Task.FromResult<EmbeddedDocumentMedia?>(null)
    };
}

public sealed class GoogleSheetsPublishedService
{
    public const int MaximumResponseBytes = 24 * 1024 * 1024;
    private readonly IHttpClientFactory _httpClients;
    private readonly IMemoryCache _cache;
    private readonly GoogleSheetsHtmlParser _parser;
    private readonly GoogleSheetsXlsxParser _xlsxParser;
    private readonly GoogleDocsImportSettings _settings;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GoogleSheetsPublishedService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ChannelEmbedDocumentDto>>> _imports = new(StringComparer.Ordinal);

    public GoogleSheetsPublishedService(IHttpClientFactory httpClients, IMemoryCache cache,
        GoogleSheetsHtmlParser parser, GoogleSheetsXlsxParser xlsxParser, GoogleDocsImportSettings settings, IHostApplicationLifetime lifetime,
        ILogger<GoogleSheetsPublishedService> logger)
    {
        _httpClients = httpClients; _cache = cache; _parser = parser; _xlsxParser = xlsxParser; _settings = settings;
        _lifetime = lifetime; _logger = logger;
    }

    public async Task<ChannelEmbedDocumentDto> GetAsync(EmbeddedContentConfiguration source,
        CancellationToken cancellationToken)
    {
        var key = FreshKey(source);
        if (_cache.TryGetValue(key, out ChannelEmbedDocumentDto? fresh) && fresh is not null) return fresh;
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(LastKey(source), out ChannelEmbedDocumentDto? last) && last is not null)
        {
            if (!_cache.TryGetValue(FailureKey(source), out _)) _ = ObserveAsync(Start(source, true).Value, source.SourceId);
            return last with { IsStale = true };
        }
        return await Start(source, false).Value.WaitAsync(cancellationToken);
    }

    public Task<ChannelEmbedDocumentDto> RefreshAsync(EmbeddedContentConfiguration source,
        CancellationToken cancellationToken) => Start(source, true).Value.WaitAsync(cancellationToken);

    private Lazy<Task<ChannelEmbedDocumentDto>> Start(EmbeddedContentConfiguration source, bool force)
    {
        Lazy<Task<ChannelEmbedDocumentDto>>? candidate = null;
        candidate = new(() => ImportSharedAsync(source, force, candidate!), LazyThreadSafetyMode.ExecutionAndPublication);
        return _imports.GetOrAdd(source.CacheIdentity, candidate);
    }

    private async Task<ChannelEmbedDocumentDto> ImportSharedAsync(EmbeddedContentConfiguration source, bool force,
        Lazy<Task<ChannelEmbedDocumentDto>> owner)
    {
        try
        {
            if (!force && _cache.TryGetValue(FreshKey(source), out ChannelEmbedDocumentDto? fresh) && fresh is not null)
                return fresh;
            return await ImportAsync(source, _lifetime.ApplicationStopping);
        }
        finally { _imports.TryRemove(new(source.CacheIdentity, owner)); }
    }

    private async Task<ChannelEmbedDocumentDto> ImportAsync(EmbeddedContentConfiguration source,
        CancellationToken stoppingToken)
    {
        var total = Stopwatch.StartNew();
        ChannelEmbedDocumentDto result;
        HttpStatusCode? statusCode = null;
        string? contentType = null;
        int? sourceBytes = null;
        long? parseMs = null;
        int? sheetCount = null;
        int? rowCount = null;
        int? cellCount = null;
        int? dtoBytes = null;
        using var timeout = new CancellationTokenSource(_settings.SourceFetchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stoppingToken);
        try
        {
            var http = _httpClients.CreateClient(GoogleDocsPublishedDocumentService.HttpClientName);
            using var response = await SendSourceAsync(http, source, linked.Token);
            statusCode = response.StatusCode;
            contentType = response.Content.Headers.ContentType?.MediaType;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                result = Result(ChannelEmbedDocumentStatus.AuthenticationRequired);
            else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                result = Result(ChannelEmbedDocumentStatus.NotFound);
            else if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest &&
                     response.Headers.Location is { IsAbsoluteUri: true } location &&
                     location.Host.EndsWith("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                result = Result(ChannelEmbedDocumentStatus.AuthenticationRequired);
            else if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not
                     ("text/html" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/octet-stream" or "application/binary"))
                result = Result((int)response.StatusCode >= 500 ? ChannelEmbedDocumentStatus.TemporaryFailure :
                    ChannelEmbedDocumentStatus.Unsupported);
            else if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                result = Result(ChannelEmbedDocumentStatus.TooLarge);
            else if (await ReadBytesAsync(response.Content, linked.Token) is not { } downloaded)
                result = Result(ChannelEmbedDocumentStatus.TooLarge);
            else
            {
                sourceBytes = downloaded.Length;
                var isXlsx = downloaded.Length >= 4 && downloaded[0] == (byte)'P' && downloaded[1] == (byte)'K';
                var html = isXlsx ? null : Encoding.UTF8.GetString(downloaded);
                if (html is not null && LooksLikeAccessFailure(html))
                {
                    result = Result(ChannelEmbedDocumentStatus.AuthenticationRequired);
                    goto Imported;
                }
                var parse = Stopwatch.StartNew();
                var xlsx = isXlsx ? _xlsxParser.ParseWithMedia(downloaded, source) : null;
                var sheet = xlsx?.Sheet ?? (isXlsx ? null : _parser.Parse(html!, source));
                parseMs = parse.ElapsedMilliseconds;
                if (sheet is null)
                {
                    result = Result(ChannelEmbedDocumentStatus.ParseFailure);
                    goto Imported;
                }
                if (isXlsx)
                    foreach (var tab in sheet.Tabs.ToArray())
                        if (await FetchDisplayValuesAsync(http, source, tab.Name, linked.Token) is { Count: > 0 } values)
                            sheet = OverlayDisplayValues(sheet, values, tab.Id);
                var discovered = isXlsx ? [] : _parser.DiscoverTabs(html!);
                if (discovered.Count > 1)
                {
                    var tabs = new List<EmbeddedSheetTabDto>();
                    foreach (var tab in discovered)
                    {
                        var existing = sheet.Tabs.FirstOrDefault(value => value.Id == tab.Id);
                        if (existing is not null) { tabs.Add(existing with { Name = tab.Name }); continue; }
                        var published = source.FetchUrl.Contains("/spreadsheets/d/e/", StringComparison.Ordinal);
                        var tabBase = published
                            ? $"https://docs.google.com/spreadsheets/d/e/{source.SourceId}/pubhtml"
                            : $"https://docs.google.com/spreadsheets/d/{source.SourceId}/gviz/tq?tqx=out:html";
                        var openBase = published ? tabBase :
                            $"https://docs.google.com/spreadsheets/d/{source.SourceId}/view";
                        var tabSource = source with
                        {
                            TabId = tab.Id,
                            OpenUrl = $"{openBase}?gid={tab.Id}",
                            FetchUrl = $"{tabBase}{(published ? "?" : "&")}gid={tab.Id}"
                        };
                        if (await FetchAdditionalTabAsync(http, tabSource, linked.Token) is { } loaded)
                            tabs.Add(loaded with { Name = tab.Name });
                    }
                    if (tabs.Count > 0) sheet = sheet with { Tabs = tabs };
                }
                var normalized = JsonSerializer.SerializeToUtf8Bytes(sheet, JsonSerializerOptions.Web);
                dtoBytes = normalized.Length;
                var fingerprint = Convert.ToHexString(SHA256.HashData(normalized));
                sheetCount = sheet.Tabs.Count;
                rowCount = sheet.Tabs.Sum(tab => tab.Rows.Count);
                cellCount = sheet.Tabs.Sum(tab => tab.Rows.Sum(row => row.Cells.Count));
                result = new(ChannelEmbedDocumentStatus.Ready, null, DateTimeOffset.UtcNow, false, fingerprint, sheet);
                _cache.Set(LastKey(source), result, _settings.StaleFor);
                if (xlsx is { Media.Count: > 0 }) _cache.Set(MediaKey(source), xlsx.Media, _settings.StaleFor);
            }
            Imported:;
        }
        catch (GoogleSheetsTooLargeException) { result = Result(ChannelEmbedDocumentStatus.TooLarge); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        { result = Result(ChannelEmbedDocumentStatus.Timeout); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Google spreadsheet {SpreadsheetId} could not be imported.", source.SourceId);
            result = Result(ChannelEmbedDocumentStatus.TemporaryFailure);
        }
        if (result.Status == ChannelEmbedDocumentStatus.Ready)
        {
            _cache.Remove(FailureKey(source));
            _cache.Set(FreshKey(source), result, _settings.FreshFor);
            LogImport(source, statusCode, contentType, sourceBytes, dtoBytes, parseMs, sheetCount, rowCount, cellCount,
                total.ElapsedMilliseconds, result.Status);
            return result;
        }
        if (_cache.TryGetValue(LastKey(source), out ChannelEmbedDocumentDto? stale) && stale is not null)
        {
            _cache.Set(FailureKey(source), true, _settings.RetryFailureAfter);
            var served = stale with { IsStale = true };
            LogImport(source, statusCode, contentType, sourceBytes, dtoBytes, parseMs, sheetCount, rowCount, cellCount,
                total.ElapsedMilliseconds, result.Status);
            return served;
        }
        _cache.Set(FreshKey(source), result, _settings.RetryFailureAfter);
        LogImport(source, statusCode, contentType, sourceBytes, dtoBytes, parseMs, sheetCount, rowCount, cellCount,
            total.ElapsedMilliseconds, result.Status);
        return result;
    }

    private async Task<EmbeddedSheetTabDto?> FetchAdditionalTabAsync(HttpClient http,
        EmbeddedContentConfiguration source, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.FetchUrl);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not "text/html" ||
                response.Content.Headers.ContentLength is > MaximumResponseBytes) return null;
            var html = await ReadAsync(response.Content, cancellationToken);
            return html is null ? null : _parser.Parse(html.Value.Text, source)?.Tabs.FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Google spreadsheet tab {TabId} could not be imported.", source.TabId);
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<(int Row, int Column), string>?> FetchDisplayValuesAsync(HttpClient http,
        EmbeddedContentConfiguration source, string tabName, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://docs.google.com/spreadsheets/d/{source.SourceId}/gviz/tq?tqx=out:json&headers=0&sheet={Uri.EscapeDataString(tabName)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json,text/javascript,application/javascript,text/plain");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumResponseBytes ||
                await ReadAsync(response.Content, cancellationToken) is not { } downloaded) return null;
            return FormattedValues(downloaded.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Google spreadsheet tab {TabName} display values could not be enriched.", tabName);
            return null;
        }
    }

    public static IReadOnlyDictionary<(int Row, int Column), string> FormattedValues(string source)
    {
        var result = new Dictionary<(int, int), string>();
        var start = source.IndexOf('{'); var end = source.LastIndexOf('}');
        if (start < 0 || end <= start) return result;
        try
        {
            using var json = JsonDocument.Parse(source.Substring(start, end - start + 1));
            if (!json.RootElement.TryGetProperty("table", out var table) ||
                !table.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return result;
            var rowIndex = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("c", out var cells) &&
                    cells.ValueKind == JsonValueKind.Array)
                {
                    var columnIndex = 0;
                    foreach (var cell in cells.EnumerateArray())
                    {
                        // The array index is the coordinate. Null cells are deliberately retained as gaps.
                        if (cell.ValueKind == JsonValueKind.Object && cell.TryGetProperty("f", out var formatted) &&
                            formatted.ValueKind == JsonValueKind.String)
                            result[(rowIndex, columnIndex)] = formatted.GetString()!;
                        columnIndex++;
                    }
                }
                rowIndex++;
            }
        }
        catch (JsonException) { }
        return result;
    }

    public static EmbeddedSheetDto OverlayDisplayValues(EmbeddedSheetDto sheet,
        IReadOnlyDictionary<(int Row, int Column), string> values,
        string? requestedTabId)
    {
        var index = requestedTabId is null ? 0 : sheet.Tabs.ToList().FindIndex(tab => tab.Id == requestedTabId);
        if (index < 0) index = 0;
        var target = sheet.Tabs[index];
        var rows = target.Rows.Select(row => row with
        {
            Cells = row.Cells.Select(cell => values.TryGetValue((cell.Row, cell.Column), out var formatted)
                ? cell with { DisplayValue = formatted }
                : cell).ToArray()
        }).ToArray();
        var tabs = sheet.Tabs.ToArray(); tabs[index] = target with { Rows = rows };
        return sheet with { Tabs = tabs };
    }

    public async Task<EmbeddedDocumentMedia?> GetMediaAsync(EmbeddedContentConfiguration source, string mediaId,
        CancellationToken cancellationToken)
    {
        if (mediaId.Length != 32 || !mediaId.All(char.IsAsciiHexDigit)) return null;
        if (!_cache.TryGetValue(MediaKey(source), out IReadOnlyDictionary<string, EmbeddedDocumentMedia>? media))
        {
            await GetAsync(source, cancellationToken);
            _cache.TryGetValue(MediaKey(source), out media);
        }
        return media is not null && media.TryGetValue(mediaId, out var value) ? value : null;
    }

    private static async Task<HttpResponseMessage> SendSourceAsync(HttpClient http,
        EmbeddedContentConfiguration source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.FetchUrl);
        request.Headers.Accept.ParseAdd(source.FetchUrl.Contains("format=xlsx", StringComparison.Ordinal)
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/html");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or
            HttpStatusCode.Found or HttpStatusCode.SeeOther) ||
            response.Headers.Location is not { } location) return response;
        if (!location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || !location.IsDefaultPort ||
            !(location.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(location.Host, "googleusercontent.com", StringComparison.OrdinalIgnoreCase)))
            return response;
        response.Dispose();
        using var redirected = new HttpRequestMessage(HttpMethod.Get, location);
        redirected.Headers.Accept.ParseAdd("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return await http.SendAsync(redirected, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<DownloadedSource?> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var writer = new ArrayBufferWriter<byte>(64 * 1024);
        while (true)
        {
            var remaining = MaximumResponseBytes - writer.WrittenCount;
            var memory = writer.GetMemory(Math.Min(64 * 1024, remaining + 1));
            var read = await stream.ReadAsync(memory[..Math.Min(memory.Length, remaining + 1)], cancellationToken);
            if (read == 0) return new(Encoding.UTF8.GetString(writer.WrittenSpan), writer.WrittenCount);
            writer.Advance(read); if (writer.WrittenCount > MaximumResponseBytes) return null;
        }
    }
    private async Task<byte[]?> ReadBytesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var writer = new ArrayBufferWriter<byte>(64 * 1024);
        while (true)
        {
            var remaining = MaximumResponseBytes - writer.WrittenCount;
            var memory = writer.GetMemory(Math.Min(64 * 1024, remaining + 1));
            var read = await stream.ReadAsync(memory[..Math.Min(memory.Length, remaining + 1)], cancellationToken);
            if (read == 0) return writer.WrittenSpan.ToArray();
            writer.Advance(read); if (writer.WrittenCount > MaximumResponseBytes) return null;
        }
    }
    internal static bool LooksLikeAccessFailure(string source) =>
        source.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("request access", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("you need access", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("accounts.google.com/ServiceLogin", StringComparison.OrdinalIgnoreCase);
    private void LogImport(EmbeddedContentConfiguration source, HttpStatusCode? statusCode, string? contentType,
        int? sourceBytes, int? dtoBytes, long? parseMs, int? sheetCount, int? rowCount, int? cellCount, long totalMs,
        ChannelEmbedDocumentStatus result) => _logger.LogDebug(
        "GoogleSheetsImport: SpreadsheetId={SpreadsheetId} Gid={Gid} InputKind={InputKind} " +
        "FetchUrlKind={FetchUrlKind} StatusCode={StatusCode} ContentType={ContentType} SourceBytes={SourceBytes} DtoBytes={DtoBytes} " +
        "ParseMs={ParseMs} SheetCount={SheetCount} RowCount={RowCount} CellCount={CellCount} TotalMs={TotalMs} Result={Result}",
        source.SourceId, source.TabId, source.OpenUrl.Contains("/d/e/", StringComparison.Ordinal) ? "Published" : "ShareLink",
        source.FetchUrl.Contains("format=xlsx", StringComparison.Ordinal) ? "WorkbookExport" : "PublishedHtml",
        statusCode is null ? null : (int)statusCode, contentType, sourceBytes, dtoBytes, parseMs, sheetCount, rowCount,
        cellCount, totalMs, result);
    private async Task ObserveAsync(Task task, string id)
    {
        try { await task; }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogDebug(exception, "Spreadsheet refresh failed for {SpreadsheetId}.", id); }
    }
    private static ChannelEmbedDocumentDto Result(ChannelEmbedDocumentStatus status) => new(status, null);
    private static string FreshKey(EmbeddedContentConfiguration source) => $"embedded-content:{source.CacheIdentity}";
    private static string LastKey(EmbeddedContentConfiguration source) => $"embedded-content-last:{source.CacheIdentity}";
    private static string FailureKey(EmbeddedContentConfiguration source) => $"embedded-content-failure:{source.CacheIdentity}";
    private static string MediaKey(EmbeddedContentConfiguration source) => $"embedded-content-media:{source.CacheIdentity}";
    private readonly record struct DownloadedSource(string Text, int Bytes);
}
