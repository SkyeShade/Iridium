using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Iridium.Protocol;
using Microsoft.Extensions.Caching.Memory;

namespace Iridium.Server.Embeds;

public sealed record EmbeddedDocumentMedia(byte[] Bytes, string ContentType);

public interface IGoogleDocsPublishedDocumentService
{
    Task<ChannelEmbedDocumentDto> GetAsync(GoogleDocsEmbedConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task<ChannelEmbedDocumentDto> RefreshAsync(GoogleDocsEmbedConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task<EmbeddedDocumentMedia?> GetMediaAsync(GoogleDocsEmbedConfiguration configuration, string mediaId,
        CancellationToken cancellationToken = default);
}

public sealed class GoogleDocsPublishedDocumentService : IGoogleDocsPublishedDocumentService
{
    public const string HttpClientName = "GoogleDocsDocumentSource";
    // Anonymous HTML exports inline images as base64. A measured 27-page document is ~31 MiB, so retain
    // enough headroom for normal lore/RP documents while keeping a firm source-memory boundary.
    public const int MaximumResponseBytes = 48 * 1024 * 1024;
    public const int MaximumMediaBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan SourceFetchTimeout = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan MediaFetchTimeout = TimeSpan.FromSeconds(10);
    private readonly IHttpClientFactory _httpClients;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly GoogleDocsDocumentParser _parser;
    private readonly ILogger<GoogleDocsPublishedDocumentService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeSpan _sourceFetchTimeout;
    private readonly TimeSpan _mediaFetchTimeout;
    private readonly TimeSpan _successTtl;
    private readonly TimeSpan _lastSuccessTtl;
    private readonly TimeSpan _failureTtl;
    private readonly ConcurrentDictionary<string, Lazy<Task<ChannelEmbedDocumentDto>>> _imports =
        new(StringComparer.Ordinal);

    public GoogleDocsPublishedDocumentService(IHttpClientFactory httpClients, IMemoryCache cache,
        TimeProvider timeProvider, GoogleDocsDocumentParser parser,
        ILogger<GoogleDocsPublishedDocumentService> logger, IHostApplicationLifetime lifetime,
        GoogleDocsImportSettings settings)
    {
        _httpClients = httpClients;
        _cache = cache;
        _timeProvider = timeProvider;
        _parser = parser;
        _logger = logger;
        _lifetime = lifetime;
        if (settings.SourceFetchTimeout <= TimeSpan.Zero || settings.MediaFetchTimeout <= TimeSpan.Zero ||
            settings.FreshFor <= TimeSpan.Zero || settings.StaleFor <= settings.FreshFor ||
            settings.RetryFailureAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "Google Docs timeouts must be positive and the stale window must exceed the fresh window.");
        _sourceFetchTimeout = settings.SourceFetchTimeout;
        _mediaFetchTimeout = settings.MediaFetchTimeout;
        _successTtl = settings.FreshFor;
        _lastSuccessTtl = settings.StaleFor;
        _failureTtl = settings.RetryFailureAfter;
    }

    public async Task<ChannelEmbedDocumentDto> GetAsync(GoogleDocsEmbedConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.FetchUrl is null) return Result(ChannelEmbedDocumentStatus.Unsupported);
        var freshKey = FreshKey(configuration.DocumentId, configuration.FetchMode);
        if (_cache.TryGetValue(freshKey, out ChannelEmbedDocumentDto? cached) && cached is not null)
        {
            LogCacheHit(configuration, cached);
            return cached;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode),
                out CachedDocument? previous) && previous is not null)
        {
            var stale = previous.Response with { IsStale = true };
            LogCacheHit(configuration, stale);
            if (!_cache.TryGetValue(FailureKey(configuration.DocumentId, configuration.FetchMode), out _))
                _ = ObserveRefreshAsync(StartSharedImport(configuration, freshKey, force: true).Value,
                    configuration.DocumentId);
            return stale;
        }
        return await StartSharedImport(configuration, freshKey, force: false).Value.WaitAsync(cancellationToken);
    }

    public async Task<ChannelEmbedDocumentDto> RefreshAsync(GoogleDocsEmbedConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.FetchUrl is null) return Result(ChannelEmbedDocumentStatus.Unsupported);
        cancellationToken.ThrowIfCancellationRequested();
        var freshKey = FreshKey(configuration.DocumentId, configuration.FetchMode);
        return await StartSharedImport(configuration, freshKey, force: true).Value.WaitAsync(cancellationToken);
    }

    private Lazy<Task<ChannelEmbedDocumentDto>> StartSharedImport(GoogleDocsEmbedConfiguration configuration,
        string freshKey, bool force)
    {
        Lazy<Task<ChannelEmbedDocumentDto>>? candidate = null;
        candidate = new(() => RunSharedImportAsync(configuration, freshKey, candidate!, force),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return _imports.GetOrAdd(freshKey, candidate);
    }

    private async Task<ChannelEmbedDocumentDto> RunSharedImportAsync(GoogleDocsEmbedConfiguration configuration,
        string freshKey, Lazy<Task<ChannelEmbedDocumentDto>> owner, bool force)
    {
        try
        {
            if (!force && _cache.TryGetValue(freshKey, out ChannelEmbedDocumentDto? cached) && cached is not null)
            {
                LogCacheHit(configuration, cached);
                return cached;
            }
            return await ImportAsync(configuration, freshKey, _lifetime.ApplicationStopping);
        }
        finally
        {
            _imports.TryRemove(new(freshKey, owner));
        }
    }

    private async Task ObserveRefreshAsync(Task<ChannelEmbedDocumentDto> refresh, string documentId)
    {
        try { await refresh; }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Background refresh failed for Google document {DocumentId}.", documentId);
        }
    }

    private async Task<ChannelEmbedDocumentDto> ImportAsync(GoogleDocsEmbedConfiguration configuration,
        string freshKey, CancellationToken cancellationToken)
    {
        var totalWatch = Stopwatch.StartNew();
        ChannelEmbedDocumentDto result;
        HttpStatusCode? statusCode = null;
        long? declaredBytes = null;
        int? sourceBytes = null;
        long? parseMilliseconds = null;
        long? downloadMilliseconds = null;
        int? dtoBytes = null;
        long? serializationMilliseconds = null;
        GoogleDocsParseMetrics? metrics = null;
        using var sourceTimeout = new CancellationTokenSource(_sourceFetchTimeout);
        using var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, sourceTimeout.Token);
        var fetchWatch = Stopwatch.StartNew();
        Stopwatch? downloadWatch = null;
        long? headersMilliseconds = null;
        string cancellationReason = "none";
        try
        {
            var http = _httpClients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, configuration.FetchUrl);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                importCancellation.Token);
            headersMilliseconds = fetchWatch.ElapsedMilliseconds;
            statusCode = response.StatusCode;
            declaredBytes = response.Content.Headers.ContentLength;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                result = Result(ChannelEmbedDocumentStatus.AuthenticationRequired);
            else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                result = Result(ChannelEmbedDocumentStatus.NotFound);
            else if (response.StatusCode is HttpStatusCode.RequestTimeout)
                result = Result(ChannelEmbedDocumentStatus.Timeout);
            else if (response.StatusCode is HttpStatusCode.TooManyRequests ||
                     (int)response.StatusCode >= 500)
                result = Result(ChannelEmbedDocumentStatus.TemporaryFailure);
            else if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest ||
                     !response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not "text/html")
                result = Result(ChannelEmbedDocumentStatus.Unsupported);
            else if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                result = new(ChannelEmbedDocumentStatus.TooLarge, null);
            else
            {
                downloadWatch = Stopwatch.StartNew();
                var source = await ReadBoundedAsync(response.Content, MaximumResponseBytes,
                    importCancellation.Token);
                downloadMilliseconds = downloadWatch.ElapsedMilliseconds;
                sourceBytes = source?.BytesRead ?? MaximumResponseBytes + 1;
                if (source is null) result = new(ChannelEmbedDocumentStatus.TooLarge, null);
                else if (LooksLikeAuthenticationOrAccessPage(source.Value.Text))
                    result = Result(ChannelEmbedDocumentStatus.AuthenticationRequired);
                else
                {
                    var parseWatch = Stopwatch.StartNew();
                    try
                    {
                        var parsed = _parser.Parse(source.Value.Text, configuration.DocumentId,
                            new(configuration.FetchUrl!));
                        parseMilliseconds = parseWatch.ElapsedMilliseconds;
                        if (parsed is null) result = Result(ChannelEmbedDocumentStatus.ParseFailure);
                        else
                        {
                            metrics = parsed.Metrics;
                            var serializationWatch = Stopwatch.StartNew();
                            var serialized = JsonSerializer.SerializeToUtf8Bytes(parsed.Document,
                                JsonSerializerOptions.Web);
                            serializationMilliseconds = serializationWatch.ElapsedMilliseconds;
                            var fingerprint = Convert.ToHexString(SHA256.HashData(serialized));
                            if (_logger.IsEnabled(LogLevel.Debug))
                                dtoBytes = serialized.Length;
                            var now = _timeProvider.GetUtcNow();
                            var media = parsed.Media;
                            var document = parsed.Document;
                            if (_cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode),
                                    out CachedDocument? existing) && existing is not null &&
                                string.Equals(existing.ContentFingerprint, fingerprint, StringComparison.Ordinal))
                            {
                                document = existing.Response.Document!;
                                media = existing.Media;
                            }
                            result = new(ChannelEmbedDocumentStatus.Ready, document, now, false, fingerprint);
                            TryCache(LastKey(configuration.DocumentId, configuration.FetchMode),
                                new CachedDocument(result, media, fingerprint), _lastSuccessTtl);
                        }
                    }
                    catch (GoogleDocsDocumentTooLargeException)
                    {
                        parseMilliseconds = parseWatch.ElapsedMilliseconds;
                        result = Result(ChannelEmbedDocumentStatus.TooLarge);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (sourceTimeout.IsCancellationRequested &&
                                                   !cancellationToken.IsCancellationRequested)
        {
            cancellationReason = "source-timeout";
            _logger.LogWarning("Google document {DocumentId} source fetch timed out after {TimeoutSeconds}s.",
                configuration.DocumentId, _sourceFetchTimeout.TotalSeconds);
            result = Result(ChannelEmbedDocumentStatus.Timeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationReason = "application-stopping";
            _logger.LogDebug("Google document {DocumentId} import canceled because the application is stopping.",
                configuration.DocumentId);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            cancellationReason = "unexpected-transport-cancellation";
            _logger.LogWarning(exception,
                "Google document {DocumentId} source transport canceled without an import timeout or application shutdown.",
                configuration.DocumentId);
            result = Result(ChannelEmbedDocumentStatus.TemporaryFailure);
        }
        catch (Exception exception)
        {
            cancellationReason = "transport-error";
            _logger.LogWarning(exception, "Google document {DocumentId} could not be imported.", configuration.DocumentId);
            result = Result(ChannelEmbedDocumentStatus.TemporaryFailure);
        }
        var importStatus = result.Status;
        var servedStale = false;
        if (result.Status != ChannelEmbedDocumentStatus.Ready &&
            _cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode),
                out CachedDocument? previous) && previous is not null)
        {
            result = previous.Response with { IsStale = true };
            servedStale = true;
        }
        if (importStatus == ChannelEmbedDocumentStatus.Ready)
        {
            _cache.Remove(FailureKey(configuration.DocumentId, configuration.FetchMode));
            TryCache(freshKey, result, _successTtl);
        }
        else if (servedStale)
            TryCache(FailureKey(configuration.DocumentId, configuration.FetchMode), true, _failureTtl);
        else
            TryCache(freshKey, result, _failureTtl);
        if (downloadWatch is not null) downloadMilliseconds ??= downloadWatch.ElapsedMilliseconds;
        _logger.LogDebug("GoogleDocsImport: DocumentId={DocumentId} CacheHit=false FetchStart=true InputKind={InputKind} FetchMode={FetchMode} " +
            "StatusCode={StatusCode} DeclaredBytes={DeclaredBytes} HeadersMs={HeadersMs} SourceBytes={SourceBytes} DownloadMs={DownloadMs} " +
            "ParseMs={ParseMs} DomNodes={DomNodes} Blocks={Blocks} Spans={Spans} Images={Images} " +
            "TextCharacters={TextCharacters} DtoBytes={DtoBytes} SerializationMs={SerializationMs} TotalMs={TotalMs} " +
            "TimeoutConfiguredMs={TimeoutConfiguredMs} CancellationReason={CancellationReason} Result={Result} ServedStale={ServedStale}",
            configuration.DocumentId, configuration.InputKind, configuration.FetchMode, statusCode is null ? null : (int)statusCode,
            declaredBytes, headersMilliseconds, sourceBytes, downloadMilliseconds, parseMilliseconds, metrics?.DomNodes, metrics?.Blocks,
            metrics?.Spans, metrics?.Images, metrics?.TextCharacters, dtoBytes, serializationMilliseconds,
            totalWatch.ElapsedMilliseconds, _sourceFetchTimeout.TotalMilliseconds, cancellationReason, importStatus,
            servedStale);
        return result;
    }

    private void LogCacheHit(GoogleDocsEmbedConfiguration configuration, ChannelEmbedDocumentDto result) =>
        _logger.LogDebug("GoogleDocsImport: DocumentId={DocumentId} CacheHit=true InputKind={InputKind} " +
            "FetchMode={FetchMode} TotalMs=0 Result={Result} ServedStale={ServedStale}", configuration.DocumentId,
            configuration.InputKind, configuration.FetchMode, result.Status, result.IsStale);

    public async Task<EmbeddedDocumentMedia?> GetMediaAsync(GoogleDocsEmbedConfiguration configuration, string mediaId,
        CancellationToken cancellationToken = default)
    {
        if (!IsMediaId(mediaId)) return null;
        if (!_cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode),
                out CachedDocument? document) || document is null)
        {
            await GetAsync(configuration, cancellationToken);
            _cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode), out document);
        }
        if (document is null || !document.Media.TryGetValue(mediaId, out var reference)) return null;
        var key = $"google-doc-media:{configuration.DocumentId}:{mediaId}";
        if (_cache.TryGetValue(key, out EmbeddedDocumentMedia? cached) && cached is not null) return cached;
        if (reference.InlineBytes is { } inlineBytes && AllowedMediaType(reference.InlineContentType) &&
            inlineBytes.Length <= MaximumMediaBytes && MatchesSignature(inlineBytes, reference.InlineContentType!))
        {
            var inlineMedia = new EmbeddedDocumentMedia(inlineBytes, reference.InlineContentType!);
            TryCache(key, inlineMedia, _lastSuccessTtl);
            return inlineMedia;
        }
        if (reference.Source is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_mediaFetchTimeout);
            var http = _httpClients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, reference.Source);
            request.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/gif");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest ||
                response.Content.Headers.ContentLength is > MaximumMediaBytes) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!AllowedMediaType(contentType) || await ReadBoundedBytesAsync(response.Content, MaximumMediaBytes, timeout.Token) is not { } bytes ||
                !MatchesSignature(bytes, contentType!)) return null;
            var media = new EmbeddedDocumentMedia(bytes, contentType!);
            TryCache(key, media, _lastSuccessTtl);
            return media;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "Google document media {MediaId} could not be fetched.", mediaId);
            return null;
        }
    }

    internal static bool LooksLikeAuthenticationOrAccessPage(string source)
    {
        var document = new HtmlParser().ParseDocument(source);
        var title = document.Title?.Trim() ?? string.Empty;
        if (title.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("request access", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("you need access", StringComparison.OrdinalIgnoreCase)) return true;
        return document.QuerySelector("form[action*='accounts.google.com'], a[href*='accounts.google.com/ServiceLogin'], " +
            "#request-access-form, [data-id='request-access'], [aria-label*='Request access']") is not null;
    }

    private static ChannelEmbedDocumentDto Result(ChannelEmbedDocumentStatus status) => new(status, null);
    private static string FreshKey(string id, GoogleDocsFetchMode mode) => $"google-doc:{mode}:{id}";
    private static string LastKey(string id, GoogleDocsFetchMode mode) => $"google-doc-last:{mode}:{id}";
    private static string FailureKey(string id, GoogleDocsFetchMode mode) => $"google-doc-refresh-failure:{mode}:{id}";
    private static bool IsMediaId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);
    private static bool AllowedMediaType(string? value) => value is "image/png" or "image/jpeg" or "image/gif" or "image/webp";
    private static bool MatchesSignature(byte[] value, string contentType) => contentType switch
    {
        "image/png" => value.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
        "image/jpeg" => value.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
        "image/gif" => value.AsSpan().StartsWith("GIF8"u8),
        "image/webp" => value.Length >= 12 && value.AsSpan(0, 4).SequenceEqual("RIFF"u8) && value.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };
    private static async Task<BoundedText?> ReadBoundedAsync(HttpContent content, int maximum,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        var initialCapacity = content.Headers.ContentLength is > 0 and <= int.MaxValue
            ? Math.Min((int)content.Headers.ContentLength.Value, maximum) : 64 * 1024;
        var destination = new ArrayBufferWriter<byte>(initialCapacity);
        while (true)
        {
            var remaining = maximum - destination.WrittenCount;
            var target = destination.GetMemory(Math.Min(64 * 1024, remaining + 1));
            var read = await source.ReadAsync(target[..Math.Min(target.Length, remaining + 1)], cancellationToken);
            if (read == 0) return new(Encoding.UTF8.GetString(destination.WrittenSpan), destination.WrittenCount);
            destination.Advance(read);
            if (destination.WrittenCount > maximum) return null;
        }
    }
    private static async Task<byte[]?> ReadBoundedBytesAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > maximum) return null;
            destination.Write(buffer, 0, read);
        }
    }
    private sealed record CachedDocument(ChannelEmbedDocumentDto Response,
        IReadOnlyDictionary<string, GoogleDocsMediaReference> Media, string ContentFingerprint);
    private readonly record struct BoundedText(string Text, int BytesRead);

    private void TryCache<T>(object key, T value, TimeSpan lifetime)
    {
        try { _cache.Set(key, value, lifetime); }
        catch (Exception exception) { _logger.LogDebug(exception, "Google document cache insertion failed for {CacheKey}.", key); }
    }

}
