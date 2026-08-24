using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record AvailableCommunityEmoji(CommunityDto Community, CommunityEmojiDto Emoji);

public sealed class CommunityEmojiService : IDisposable
{
    private readonly NodeSession _session;
    private readonly Dictionary<Guid, IReadOnlyList<CommunityEmojiDto>> _collections = [];
    private readonly Dictionary<(Guid Id, long Revision), Task<string?>> _media = [];
    private readonly Dictionary<Guid, CommunityEmojiDto> _references = [];
    private Guid? _accountId;
    public event Action<Guid>? Changed;

    public CommunityEmojiService(NodeSession session)
    {
        _session = session;
        _session.CommunityChanged += OnCommunityChanged;
        _session.Changed += OnSessionChanged;
        _accountId = session.Account?.Id;
    }

    public IReadOnlyList<CommunityEmojiDto> GetCached(Guid communityId) =>
        _collections.GetValueOrDefault(communityId) ?? [];

    public async Task<IReadOnlyList<CommunityEmojiDto>> GetAsync(Guid communityId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        EnsureAccountCache();
        if (!refresh && _collections.TryGetValue(communityId, out var cached)) return cached;
        var values = await _session.GetCommunityEmojisAsync(communityId, cancellationToken);
        _collections[communityId] = values;
        return values;
    }

    public async Task<IReadOnlyList<AvailableCommunityEmoji>> GetAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureAccountCache();
        var communities = _session.Communities.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id).ToArray();
        var collections = await Task.WhenAll(communities.Select(async community =>
            (Community: community, Emojis: await GetAsync(community.Id, cancellationToken: cancellationToken))));
        return collections.SelectMany(value => value.Emojis.Select(emoji =>
            new AvailableCommunityEmoji(value.Community, emoji))).ToArray();
    }

    public async Task<string?> GetMediaDataUrlAsync(CommunityEmojiDto emoji, CancellationToken cancellationToken = default)
    {
        EnsureAccountCache();
        var key = (emoji.Id, emoji.Revision);
        if (_media.TryGetValue(key, out var cached)) return await cached;
        var download = DownloadMediaAsync(emoji, cancellationToken);
        _media[key] = download;
        return await download;
    }

    private async Task<string?> DownloadMediaAsync(CommunityEmojiDto emoji, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _session.DownloadCommunityEmojiAsync(
                emoji.CommunityId, emoji.Id, emoji.Revision, cancellationToken);
            return $"data:{emoji.ContentType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }

    public async Task<CommunityEmojiDto?> ResolveReferenceAsync(Guid emojiId, CancellationToken cancellationToken = default)
    {
        EnsureAccountCache();
        var memberEmoji = _collections.Values.SelectMany(value => value).FirstOrDefault(value => value.Id == emojiId);
        if (memberEmoji is not null) return memberEmoji;
        if (_references.TryGetValue(emojiId, out var cached)) return cached;
        try
        {
            var emoji = await _session.AuthorizedClient.GetCommunityEmojiReferenceAsync(emojiId, cancellationToken);
            _references[emojiId] = emoji;
            return emoji;
        }
        catch { return null; }
    }

    private void OnCommunityChanged(CommunityStateChangedEvent change)
    {
        if (change.Change != "expressions-updated") return;
        foreach (var key in _media.Keys.Where(key =>
                     _collections.GetValueOrDefault(change.CommunityId)?.Any(emoji => emoji.Id == key.Id) == true).ToArray())
            _media.Remove(key);
        _collections.Remove(change.CommunityId);
        _ = RefreshFromRealtimeAsync(change.CommunityId);
    }

    private async Task RefreshFromRealtimeAsync(Guid communityId)
    {
        try { await GetAsync(communityId, true); }
        catch { }
        Changed?.Invoke(communityId);
    }

    private void OnSessionChanged() => EnsureAccountCache();

    private void EnsureAccountCache()
    {
        var accountId = _session.Account?.Id;
        if (_accountId == accountId) return;
        _accountId = accountId;
        _collections.Clear();
        _media.Clear();
        _references.Clear();
    }

    public void Dispose()
    {
        _session.CommunityChanged -= OnCommunityChanged;
        _session.Changed -= OnSessionChanged;
    }
}
