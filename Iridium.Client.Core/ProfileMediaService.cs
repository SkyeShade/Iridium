using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed class ProfileMediaService : IDisposable
{
    private readonly NodeSession _session;
    private readonly Dictionary<Guid, long> _avatarRevisions = [];
    private readonly Dictionary<Guid, ProfileAvatarDto> _presentations = [];
    private readonly Dictionary<(Guid AccountId, Guid PresetId), ProfileAvatarDto> _presetPresentations = [];
    private readonly Dictionary<Guid, ProfileAvatarDto> _messageSnapshots = [];
    private readonly Dictionary<Guid, long> _bannerRevisions = [];
    private readonly Dictionary<Guid, ProfileBannerDto> _banners = [];
    public event Action<Guid>? Changed;

    public ProfileMediaService(NodeSession session)
    {
        _session = session;
        session.ProfileUpdated += OnProfileUpdated;
    }

    public void Observe(Guid accountId, long revision)
    {
        if (revision <= 0 || _avatarRevisions.GetValueOrDefault(accountId) >= revision) return;
        _avatarRevisions[accountId] = revision;
    }

    public void Invalidate(Guid accountId, long revision)
    {
        if (revision > _avatarRevisions.GetValueOrDefault(accountId))
            _avatarRevisions[accountId] = revision;
        _presentations.Remove(accountId);
        foreach (var key in _presetPresentations.Keys.Where(value => value.AccountId == accountId).ToArray())
            _presetPresentations.Remove(key);
        Changed?.Invoke(accountId);
    }

    public string? AvatarUrl(Guid accountId, long observedRevision = 0)
    {
        Observe(accountId, observedRevision);
        if (!_session.IsAuthenticated) return null;
        var revision = _avatarRevisions.GetValueOrDefault(accountId, observedRevision);
        return new Uri(_session.AuthorizedClient.NodeAddress,
            $"api/profiles/{accountId}/avatar{(revision > 0 ? $"?v={revision}" : string.Empty)}").ToString();
    }

    public async Task<ProfileAvatarDto?> GetAvatarAsync(Guid accountId, long observedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        Observe(accountId, observedRevision);
        if (_presentations.TryGetValue(accountId, out var cached) &&
            cached.Revision >= _avatarRevisions.GetValueOrDefault(accountId)) return cached;
        try
        {
            var value = await _session.AuthorizedClient.GetProfileAvatarAsync(accountId, cancellationToken);
            _presentations[accountId] = value;
            Observe(accountId, value.Revision);
            return value;
        }
        catch { return null; }
    }

    public async Task<ProfileAvatarDto?> GetAvatarPresetAsync(Guid accountId, Guid presetId,
        long observedRevision = 0, CancellationToken cancellationToken = default)
    {
        var key = (accountId, presetId);
        if (_presetPresentations.TryGetValue(key, out var cached) && cached.Revision >= observedRevision) return cached;
        try
        {
            var value = await _session.AuthorizedClient.GetProfileAvatarPresetAsync(accountId, presetId, cancellationToken);
            _presetPresentations[key] = value;
            return value;
        }
        catch { return null; }
    }

    public async Task<ProfileAvatarDto?> GetMessageSnapshotAsync(Guid messageId,
        CancellationToken cancellationToken = default)
    {
        if (_messageSnapshots.TryGetValue(messageId, out var cached)) return cached;
        try
        {
            var value = await _session.AuthorizedClient.GetMessageAuthorAvatarSnapshotAsync(messageId, cancellationToken);
            _messageSnapshots[messageId] = value;
            return value;
        }
        catch { return null; }
    }

    public void PrimeMessageSnapshot(Guid messageId, MessageAvatarSnapshotDto snapshot)
    {
        if (_messageSnapshots.ContainsKey(messageId)) return;
        var url = new Uri(_session.AuthorizedClient.NodeAddress,
            $"api/messages/{messageId}/author-avatar?v={snapshot.Revision}").ToString();
        _messageSnapshots[messageId] = new ProfileAvatarDto(true, url, snapshot.Revision,
            snapshot.CropX, snapshot.CropY, snapshot.Zoom, snapshot.Width, snapshot.Height);
    }

    public void ObserveBanner(Guid accountId, long revision)
    {
        if (revision > _bannerRevisions.GetValueOrDefault(accountId)) _bannerRevisions[accountId] = revision;
    }

    public async Task<ProfileBannerDto?> GetBannerAsync(Guid accountId, long observedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        ObserveBanner(accountId, observedRevision);
        if (_banners.TryGetValue(accountId, out var cached) &&
            cached.Revision >= _bannerRevisions.GetValueOrDefault(accountId)) return cached;
        if (!_session.IsAuthenticated) return null;
        try
        {
            var value = await _session.AuthorizedClient.GetProfileBannerAsync(accountId, cancellationToken);
            _banners[accountId] = value;
            ObserveBanner(accountId, value.Revision);
            return value;
        }
        catch { return null; }
    }

    public void InvalidateBanner(Guid accountId, long revision)
    {
        ObserveBanner(accountId, revision);
        _banners.Remove(accountId);
        Changed?.Invoke(accountId);
    }

    private void OnProfileUpdated(ProfileUpdatedEvent change)
    {
        var changed = false;
        if (_avatarRevisions.GetValueOrDefault(change.AccountId) < change.AvatarRevision)
        {
            _avatarRevisions[change.AccountId] = change.AvatarRevision;
            _presentations.Remove(change.AccountId);
            foreach (var key in _presetPresentations.Keys.Where(value => value.AccountId == change.AccountId).ToArray())
                _presetPresentations.Remove(key);
            changed = true;
        }
        if (_bannerRevisions.GetValueOrDefault(change.AccountId) < change.BannerRevision)
        {
            _bannerRevisions[change.AccountId] = change.BannerRevision;
            _banners.Remove(change.AccountId);
            changed = true;
        }
        if (changed) Changed?.Invoke(change.AccountId);
    }

    public void Dispose() => _session.ProfileUpdated -= OnProfileUpdated;
}
