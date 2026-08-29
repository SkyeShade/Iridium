using Iridium.Protocol;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed record AvailableCommunityEmoji(CommunityDto Community, CommunityEmojiDto Emoji);

public sealed class CommunityEmojiService : IDisposable
{
    private readonly NodeSession _session;
    private readonly ILogger<CommunityEmojiService>? _logger;
    private readonly Dictionary<Guid, IReadOnlyList<CommunityEmojiDto>> _collections = [];
    private readonly Dictionary<(Guid Id, long Revision), Task<string?>> _media = [];
    private readonly Dictionary<Guid, CommunityEmojiDto> _references = [];
    private Guid? _accountId;
    private string? _nodeAddress;
    private HashSet<Guid> _communityIds = [];
    public event Action<Guid>? Changed;

    public CommunityEmojiService(NodeSession session, ILogger<CommunityEmojiService>? logger = null)
    {
        _session = session;
        _logger = logger;
        _session.CommunityChanged += OnCommunityChanged;
        _session.Changed += OnSessionChanged;
        _accountId = session.Account?.Id;
        _nodeAddress = session.SelectedNode?.Address;
        _communityIds = session.Communities.Select(value => value.Id).ToHashSet();
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
        CommunityDto? requiredCommunity = null, CancellationToken cancellationToken = default)
    {
        EnsureAccountCache();
        var communities = _session.Communities
            .Concat(requiredCommunity is null || _session.Communities.Any(value => value.Id == requiredCommunity.Id)
                ? [] : [requiredCommunity])
            .DistinctBy(value => value.Id)
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id).ToArray();
        var collections = await Task.WhenAll(communities.Select(community =>
            LoadAvailableCollectionAsync(community, cancellationToken)));
        return collections.SelectMany(value => value.Emojis.Select(emoji =>
            new AvailableCommunityEmoji(value.Community, emoji))).ToArray();
    }

    private async Task<(CommunityDto Community, IReadOnlyList<CommunityEmojiDto> Emojis)>
        LoadAvailableCollectionAsync(CommunityDto community, CancellationToken cancellationToken)
    {
        try
        {
            return (community, await GetAsync(community.Id, cancellationToken: cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception,
                "Could not load custom emoji for Server {CommunityId}; other emoji sources remain available.",
                community.Id);
            return (community, []);
        }
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

    public async Task<string?> GetReactionMediaDataUrlAsync(ReactionEmojiDto emoji,
        CancellationToken cancellationToken = default)
    {
        if (emoji.CustomEmojiId is not { } id || !emoji.CustomEmojiAvailable) return null;
        EnsureAccountCache();
        var key = (id, emoji.CustomEmojiRevision);
        if (_media.TryGetValue(key, out var cached)) return await cached;
        async Task<string?> Download()
        {
            try
            {
                var bytes = await _session.AuthorizedClient.DownloadCommunityEmojiReferenceAsync(id,
                    emoji.CustomEmojiRevision, cancellationToken);
                return $"data:{emoji.CustomEmojiContentType ?? "image/png"};base64,{Convert.ToBase64String(bytes)}";
            }
            catch { return null; }
        }
        var task = Download();
        _media[key] = task;
        return await task;
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

    private void OnSessionChanged()
    {
        if (!EnsureAccountCache()) return;
        Changed?.Invoke(Guid.Empty);
    }

    private bool EnsureAccountCache()
    {
        var accountId = _session.Account?.Id;
        var nodeAddress = _session.SelectedNode?.Address;
        var communityIds = _session.Communities.Select(value => value.Id).ToHashSet();
        var accountChanged = _accountId != accountId ||
            !string.Equals(_nodeAddress, nodeAddress, StringComparison.OrdinalIgnoreCase);
        var membershipsChanged = !_communityIds.SetEquals(communityIds);
        if (!accountChanged && !membershipsChanged) return false;
        _accountId = accountId;
        _nodeAddress = nodeAddress;
        if (accountChanged)
        {
            _collections.Clear();
            _media.Clear();
            _references.Clear();
        }
        else
        {
            foreach (var removed in _communityIds.Except(communityIds).ToArray()) _collections.Remove(removed);
        }
        _communityIds = communityIds;
        return true;
    }

    public void Dispose()
    {
        _session.CommunityChanged -= OnCommunityChanged;
        _session.Changed -= OnSessionChanged;
    }
}
