using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record AvailableCommunityEmoji(CommunityDto Community, CommunityEmojiDto Emoji);

public sealed class CommunityEmojiService : IDisposable
{
    private readonly NodeSession _session;
    private readonly Dictionary<Guid, IReadOnlyList<CommunityEmojiDto>> _collections = [];
    private readonly Dictionary<Guid, string> _media = [];
    private readonly Dictionary<Guid, CommunityEmojiDto> _references = [];
    public event Action<Guid>? Changed;

    public CommunityEmojiService(NodeSession session)
    {
        _session = session;
        _session.CommunityChanged += OnCommunityChanged;
    }

    public IReadOnlyList<CommunityEmojiDto> GetCached(Guid communityId) =>
        _collections.GetValueOrDefault(communityId) ?? [];

    public async Task<IReadOnlyList<CommunityEmojiDto>> GetAsync(Guid communityId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _collections.TryGetValue(communityId, out var cached)) return cached;
        var values = await _session.GetCommunityEmojisAsync(communityId, cancellationToken);
        _collections[communityId] = values;
        return values;
    }

    public async Task<IReadOnlyList<AvailableCommunityEmoji>> GetAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        var communities = _session.Communities.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id).ToArray();
        var collections = await Task.WhenAll(communities.Select(async community =>
            (Community: community, Emojis: await GetAsync(community.Id, cancellationToken: cancellationToken))));
        return collections.SelectMany(value => value.Emojis.Select(emoji =>
            new AvailableCommunityEmoji(value.Community, emoji))).ToArray();
    }

    public async Task<string?> GetMediaDataUrlAsync(CommunityEmojiDto emoji, CancellationToken cancellationToken = default)
    {
        if (_media.TryGetValue(emoji.Id, out var cached)) return cached;
        try
        {
            var bytes = await _session.DownloadCommunityEmojiAsync(emoji.CommunityId, emoji.Id, cancellationToken);
            var value = $"data:{emoji.ContentType};base64,{Convert.ToBase64String(bytes)}";
            _media[emoji.Id] = value;
            return value;
        }
        catch { return null; }
    }

    public async Task<CommunityEmojiDto?> ResolveReferenceAsync(Guid emojiId, CancellationToken cancellationToken = default)
    {
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
        _collections.Remove(change.CommunityId);
        _ = RefreshFromRealtimeAsync(change.CommunityId);
    }

    private async Task RefreshFromRealtimeAsync(Guid communityId)
    {
        try { await GetAsync(communityId, true); }
        catch { }
        Changed?.Invoke(communityId);
    }

    public void Dispose() => _session.CommunityChanged -= OnCommunityChanged;
}
