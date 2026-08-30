using System.Buffers;
using System.Diagnostics;
using System.Net;
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
    Task<EmbeddedDocumentMedia?> GetMediaAsync(GoogleDocsEmbedConfiguration configuration, string mediaId,
        CancellationToken cancellationToken = default);
}

public sealed class GoogleDocsPublishedDocumentService(HttpClient http, IMemoryCache cache, TimeProvider timeProvider,
    GoogleDocsDocumentParser parser, ILogger<GoogleDocsPublishedDocumentService> logger) : IGoogleDocsPublishedDocumentService
{
    // Anonymous HTML exports inline images as base64. A measured 27-page document is ~31 MiB, so retain
    // enough headroom for normal lore/RP documents while keeping a firm source-memory boundary.
    public const int MaximumResponseBytes = 48 * 1024 * 1024;
    public const int MaximumMediaBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan SourceFetchTimeout = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan MediaFetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LastSuccessTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    public async Task<ChannelEmbedDocumentDto> GetAsync(GoogleDocsEmbedConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.FetchUrl is null) return Result(ChannelEmbedDocumentStatus.Unsupported);
        var freshKey = FreshKey(configuration.DocumentId, configuration.FetchMode);
        if (cache.TryGetValue(freshKey, out ChannelEmbedDocumentDto? cached) && cached is not null) return cached;
        ChannelEmbedDocumentDto result;
        HttpStatusCode? statusCode = null;
        long? declaredBytes = null;
        int? sourceBytes = null;
        long? parseMilliseconds = null;
        long? fetchMilliseconds = null;
        int? dtoBytes = null;
        GoogleDocsParseMetrics? metrics = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SourceFetchTimeout);
        var fetchWatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, configuration.FetchUrl);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
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
                var source = await ReadBoundedAsync(response.Content, MaximumResponseBytes, timeout.Token);
                fetchMilliseconds = fetchWatch.ElapsedMilliseconds;
                sourceBytes = source?.BytesRead ?? MaximumResponseBytes + 1;
                if (source is null) result = new(ChannelEmbedDocumentStatus.TooLarge, null);
                else if (LooksLikeAuthenticationOrAccessPage(source.Value.Text))
                    result = Result(ChannelEmbedDocumentStatus.AuthenticationRequired);
                else
                {
                    var parseWatch = Stopwatch.StartNew();
                    try
                    {
                        var parsed = parser.Parse(source.Value.Text, configuration.DocumentId,
                            new(configuration.FetchUrl));
                        parseMilliseconds = parseWatch.ElapsedMilliseconds;
                        if (parsed is null) result = Result(ChannelEmbedDocumentStatus.ParseFailure);
                        else
                        {
                            metrics = parsed.Metrics;
                            if (logger.IsEnabled(LogLevel.Debug))
                                dtoBytes = MeasureDtoBytes(parsed.Document);
                            var now = timeProvider.GetUtcNow();
                            result = new(ChannelEmbedDocumentStatus.Ready, parsed.Document, now);
                            TryCache(LastKey(configuration.DocumentId, configuration.FetchMode),
                                new CachedDocument(result, parsed.Media), LastSuccessTtl);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Result(ChannelEmbedDocumentStatus.Timeout);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Google document {DocumentId} could not be imported.", configuration.DocumentId);
            result = Result(ChannelEmbedDocumentStatus.TemporaryFailure);
        }
        var importStatus = result.Status;
        var servedStale = false;
        if (result.Status != ChannelEmbedDocumentStatus.Ready &&
            cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode),
                out CachedDocument? previous) && previous is not null)
        {
            result = previous.Response with { IsStale = true };
            servedStale = true;
        }
        TryCache(freshKey, result, result.Status == ChannelEmbedDocumentStatus.Ready ? SuccessTtl : FailureTtl);
        fetchMilliseconds ??= fetchWatch.ElapsedMilliseconds;
        logger.LogDebug("GoogleDocsImport: DocumentId={DocumentId} InputKind={InputKind} FetchMode={FetchMode} " +
            "StatusCode={StatusCode} DeclaredBytes={DeclaredBytes} SourceBytes={SourceBytes} FetchMs={FetchMs} " +
            "ParseMs={ParseMs} DomNodes={DomNodes} Blocks={Blocks} Spans={Spans} Images={Images} " +
            "TextCharacters={TextCharacters} DtoBytes={DtoBytes} Result={Result} ServedStale={ServedStale}",
            configuration.DocumentId, configuration.InputKind, configuration.FetchMode, statusCode is null ? null : (int)statusCode,
            declaredBytes, sourceBytes, fetchMilliseconds, parseMilliseconds, metrics?.DomNodes, metrics?.Blocks,
            metrics?.Spans, metrics?.Images, metrics?.TextCharacters, dtoBytes, importStatus, servedStale);
        return result;
    }

    public async Task<EmbeddedDocumentMedia?> GetMediaAsync(GoogleDocsEmbedConfiguration configuration, string mediaId,
        CancellationToken cancellationToken = default)
    {
        if (!IsMediaId(mediaId)) return null;
        if (!cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode),
                out CachedDocument? document) || document is null)
        {
            await GetAsync(configuration, cancellationToken);
            cache.TryGetValue(LastKey(configuration.DocumentId, configuration.FetchMode), out document);
        }
        if (document is null || !document.Media.TryGetValue(mediaId, out var reference)) return null;
        var key = $"google-doc-media:{configuration.DocumentId}:{mediaId}";
        if (cache.TryGetValue(key, out EmbeddedDocumentMedia? cached) && cached is not null) return cached;
        if (reference.InlineBytes is { } inlineBytes && AllowedMediaType(reference.InlineContentType) &&
            inlineBytes.Length <= MaximumMediaBytes && MatchesSignature(inlineBytes, reference.InlineContentType!))
        {
            var inlineMedia = new EmbeddedDocumentMedia(inlineBytes, reference.InlineContentType!);
            TryCache(key, inlineMedia, LastSuccessTtl);
            return inlineMedia;
        }
        if (reference.Source is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MediaFetchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, reference.Source);
            request.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/gif");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest ||
                response.Content.Headers.ContentLength is > MaximumMediaBytes) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!AllowedMediaType(contentType) || await ReadBoundedBytesAsync(response.Content, MaximumMediaBytes, timeout.Token) is not { } bytes ||
                !MatchesSignature(bytes, contentType!)) return null;
            var media = new EmbeddedDocumentMedia(bytes, contentType!);
            TryCache(key, media, LastSuccessTtl);
            return media;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Google document media {MediaId} could not be fetched.", mediaId);
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
        IReadOnlyDictionary<string, GoogleDocsMediaReference> Media);
    private readonly record struct BoundedText(string Text, int BytesRead);

    private void TryCache<T>(object key, T value, TimeSpan lifetime)
    {
        try { cache.Set(key, value, lifetime); }
        catch (Exception exception) { logger.LogDebug(exception, "Google document cache insertion failed for {CacheKey}.", key); }
    }

    private int? MeasureDtoBytes(EmbeddedDocumentDto document)
    {
        try { return JsonSerializer.SerializeToUtf8Bytes(document, JsonSerializerOptions.Web).Length; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Google document DTO size measurement failed.");
            return null;
        }
    }
}
