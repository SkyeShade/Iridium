using Iridium.Protocol;

namespace Iridium.Client.Core;

public interface IWebRtcConfigurationProvider
{
    Task<WebRtcIceConfigurationDto> GetAsync(CancellationToken cancellationToken = default);
}

public interface IWebRtcConfigurationSource
{
    string CacheKey { get; }
    Task<WebRtcIceConfigurationDto> FetchAsync(CancellationToken cancellationToken = default);
}

public sealed class WebRtcConfigurationProvider(IWebRtcConfigurationSource source, TimeProvider timeProvider)
    : IWebRtcConfigurationProvider, IDisposable
{
    private static readonly TimeSpan StunOnlyCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumRefreshLead = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebRtcIceConfigurationDto? _cached;
    private DateTimeOffset _refreshAt;
    private string? _cacheKey;

    public async Task<WebRtcIceConfigurationDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var key = source.CacheKey;
        var now = timeProvider.GetUtcNow();
        if (_cached is not null && string.Equals(key, _cacheKey, StringComparison.OrdinalIgnoreCase) && now < _refreshAt)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (_cached is not null && string.Equals(key, _cacheKey, StringComparison.OrdinalIgnoreCase) && now < _refreshAt)
                return _cached;
            var value = await source.FetchAsync(cancellationToken);
            _cached = value;
            _cacheKey = key;
            _refreshAt = RefreshAt(value, now);
            return value;
        }
        finally { _gate.Release(); }
    }

    private static DateTimeOffset RefreshAt(WebRtcIceConfigurationDto value, DateTimeOffset now)
    {
        if (value.ExpiresAt is not { } expiry) return now + StunOnlyCacheLifetime;
        var remaining = expiry - now;
        var lead = remaining <= TimeSpan.Zero ? TimeSpan.Zero :
            TimeSpan.FromTicks(Math.Min(MaximumRefreshLead.Ticks, remaining.Ticks / 5));
        return expiry - lead;
    }

    public void Dispose() => _gate.Dispose();
}
