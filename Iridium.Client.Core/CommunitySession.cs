using Iridium.Protocol;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class CommunitySession : IDisposable
{
    private readonly NodeSession _nodeSession;
    private readonly ILogger<CommunitySession>? _logger;
    private readonly List<CommunityCategoryDto> _categories = [];
    private readonly List<CommunityChannelDto> _channels = [];
    private readonly object _realtimeGate = new();
    private long _requestedRevision;
    private long _appliedRevision;
    private long _generation;
    private bool _realtimeRefreshRunning;
    private bool _managementRefreshRequested;
    private bool _permissionSaveInProgress;

    public CommunitySession(NodeSession nodeSession, ILogger<CommunitySession>? logger = null)
    {
        _nodeSession = nodeSession;
        _logger = logger;
        nodeSession.CommunityChanged += OnCommunityChanged;
        nodeSession.RealtimeReconnected += OnRealtimeReconnected;
    }

    public event Action? Changed;
    public event Action<Exception>? RealtimeRefreshFailed;

    public Guid? CommunityId { get; private set; }
    public bool CanManage { get; private set; }
    public CommunityPermission EffectivePermissions { get; private set; }
    public bool IsOwner { get; private set; }
    public bool CanManagePermissions { get; private set; }
    public CommunityManagementDto? Management { get; private set; }
    public IReadOnlyList<CommunityCategoryDto> Categories => _categories;
    public IReadOnlyList<CommunityChannelDto> Channels => _channels;
    public long AppliedRevision { get { lock (_realtimeGate) return _appliedRevision; } }

    public async Task LoadAsync(Guid communityId, CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_realtimeGate)
        {
            if (CommunityId != communityId) _generation++;
            generation = _generation;
        }
        var structure = await _nodeSession.AuthorizedClient.GetCommunityStructureAsync(communityId, cancellationToken);
        lock (_realtimeGate)
            if (generation != _generation) return;
        CommunityId = communityId;
        CanManage = structure.CanManage;
        EffectivePermissions = structure.EffectivePermissions;
        IsOwner = structure.IsOwner;
        CanManagePermissions = structure.CanManagePermissions;
        Replace(structure);
    }

    public bool HasPermission(CommunityPermission permission) =>
        IsOwner || (EffectivePermissions & CommunityPermission.Administrator) != 0 ||
        (EffectivePermissions & permission) == permission;

    public async Task<CommunityManagementDto> LoadManagementAsync(CancellationToken cancellationToken = default)
    {
        Management = await _nodeSession.AuthorizedClient.GetCommunityManagementAsync(RequireCommunity(), cancellationToken);
        EffectivePermissions = Management.Access.Permissions;
        IsOwner = Management.Access.IsOwner;
        CanManage = HasPermission(CommunityPermission.ManageChannels);
        CanManagePermissions = HasPermission(CommunityPermission.ManagePermissions);
        return Management;
    }

    public async Task<CommunityDto> UpdateCommunityAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        var updated = await _nodeSession.AuthorizedClient.UpdateCommunityAsync(
            RequireCommunity(), new UpdateCommunityRequest(name, description), cancellationToken);
        await LoadManagementAsync(cancellationToken);
        return updated;
    }

    public async Task CreateRoleAsync(string name, CommunityPermission permissions, string? color, bool displaySeparately = false, bool isMentionable = false, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.CreateCommunityRoleAsync(RequireCommunity(), new(name, permissions, color, displaySeparately, isMentionable), cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task UpdateRoleAsync(Guid roleId, string name, CommunityPermission permissions, string? color, bool displaySeparately = false, bool isMentionable = false, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.UpdateCommunityRoleAsync(RequireCommunity(), roleId, new(name, permissions, color, displaySeparately, isMentionable), cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.DeleteCommunityRoleAsync(RequireCommunity(), roleId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task MoveRoleAsync(Guid roleId, int position, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.MoveCommunityRoleAsync(RequireCommunity(), roleId, position, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task SetMemberRolesAsync(Guid accountId, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.SetCommunityMemberRolesAsync(RequireCommunity(), accountId, roleIds, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task KickMemberAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.KickCommunityMemberAsync(RequireCommunity(), accountId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public Task LeaveAsync(CancellationToken cancellationToken = default) =>
        _nodeSession.AuthorizedClient.LeaveCommunityAsync(RequireCommunity(), cancellationToken);

    public async Task BanMemberAsync(Guid accountId, string? reason, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.BanCommunityMemberAsync(RequireCommunity(), accountId, reason, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task UnbanMemberAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.UnbanCommunityMemberAsync(RequireCommunity(), accountId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task<CommunityInviteDto> CreateInviteAsync(DateTimeOffset? expiresAt, int? maxUses, CancellationToken cancellationToken = default)
    {
        var invite = await _nodeSession.AuthorizedClient.CreateCommunityInviteAsync(
            RequireCommunity(), new(expiresAt, maxUses), cancellationToken);
        await LoadManagementAsync(cancellationToken);
        return invite;
    }

    public async Task RevokeInviteAsync(Guid inviteId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.RevokeCommunityInviteAsync(RequireCommunity(), inviteId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task<CommunityCategoryDto> CreateCategoryAsync(string name, Guid? parentCategoryId = null, CancellationToken cancellationToken = default)
    {
        var created = await _nodeSession.AuthorizedClient.CreateCategoryAsync(RequireCommunity(), name, parentCategoryId, cancellationToken);
        _categories.Add(created); Sort(); return created;
    }

    public async Task UpdateCategoryAsync(Guid categoryId, string name, CancellationToken cancellationToken = default)
    {
        var updated = await _nodeSession.AuthorizedClient.UpdateCategoryAsync(RequireCommunity(), categoryId, name, cancellationToken);
        ReplaceCategory(updated);
    }

    public async Task MoveCategoryAsync(Guid categoryId, CommunitySidebarMoveRequest request, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.MoveCategoryAsync(RequireCommunity(), categoryId, request, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.DeleteCategoryAsync(RequireCommunity(), categoryId, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task<CommunityChannelDto> CreateChannelAsync(string name, Guid? categoryId,
        CommunityChannelKind kind = CommunityChannelKind.Text, CancellationToken cancellationToken = default)
    {
        var created = await _nodeSession.AuthorizedClient.CreateChannelAsync(RequireCommunity(), name, categoryId, kind, cancellationToken);
        _channels.Add(created); Sort(); return created;
    }

    public async Task UpdateChannelAsync(Guid channelId, string name, Guid? categoryId,
        CommunityChannelKind kind = CommunityChannelKind.Text, bool? requireTag = null,
        CommunityChannelEmbedUpdate? embed = null,
        bool? allowDocumentEmbeds = null,
        CancellationToken cancellationToken = default)
    {
        var updated = await _nodeSession.AuthorizedClient.UpdateChannelAsync(RequireCommunity(), channelId, name,
            categoryId, kind, requireTag, embed, allowDocumentEmbeds, cancellationToken);
        ReplaceChannel(updated);
    }

    public async Task MoveChannelAsync(Guid channelId, CommunitySidebarMoveRequest request, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.MoveChannelAsync(RequireCommunity(), channelId, request, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task DeleteChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.DeleteChannelAsync(RequireCommunity(), channelId, cancellationToken);
        _channels.RemoveAll(value => value.Id == channelId);
    }

    public Task<PermissionOverwriteScopeDto> GetPermissionScopeAsync(PermissionOverwriteScopeType scopeType,
        Guid scopeId, CancellationToken cancellationToken = default) =>
        _nodeSession.AuthorizedClient.GetPermissionScopeAsync(RequireCommunity(), scopeType, scopeId, cancellationToken);

    public async Task SetPermissionOverwriteAsync(PermissionOverwriteScopeType scopeType, Guid scopeId,
        SetPermissionOverwriteRequest request, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.SetPermissionOverwriteAsync(RequireCommunity(), scopeType, scopeId,
            request, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task<PermissionOverwriteSaveResultDto> ReplacePermissionOverwritesAsync(PermissionOverwriteScopeType scopeType, Guid scopeId,
        IReadOnlyList<PermissionOverwriteDto> overwrites, CancellationToken cancellationToken = default)
    {
        var communityId = RequireCommunity();
        lock (_realtimeGate)
        {
            if (_permissionSaveInProgress)
                throw new InvalidOperationException("A permission save is already in progress.");
            _permissionSaveInProgress = true;
        }
        _logger?.LogDebug(
            "PermissionSaveRequestSent CommunityId={CommunityId} ScopeId={ScopeId} ScopeType={ScopeType} CurrentRevision={Revision}",
            communityId, scopeId, scopeType, AppliedRevision);
        try
        {
            var result = await _nodeSession.AuthorizedClient.ReplacePermissionOverwritesAsync(communityId, scopeType,
                scopeId, new(overwrites), cancellationToken);
            _logger?.LogDebug(
                "PermissionSaveResponseSucceeded CommunityId={CommunityId} ScopeId={ScopeId} ScopeType={ScopeType} IncomingRevision={Revision}",
                communityId, scopeId, scopeType, result.Revision);

            var structure = await _nodeSession.AuthorizedClient.GetCommunityStructureAsync(communityId, cancellationToken);
            bool refreshDeferred;
            lock (_realtimeGate)
            {
                if (CommunityId == communityId)
                {
                    CanManage = structure.CanManage;
                    EffectivePermissions = structure.EffectivePermissions;
                    IsOwner = structure.IsOwner;
                    CanManagePermissions = structure.CanManagePermissions;
                    Replace(structure);
                }
                _appliedRevision = Math.Max(_appliedRevision, result.Revision);
                _permissionSaveInProgress = false;
                refreshDeferred = _requestedRevision > _appliedRevision;
                if (!refreshDeferred) _managementRefreshRequested = false;
            }
            Changed?.Invoke();
            if (refreshDeferred) QueueRealtimeRefresh(_requestedRevision, _managementRefreshRequested);
            return result;
        }
        catch (Exception exception)
        {
            bool refreshDeferred;
            lock (_realtimeGate)
            {
                _permissionSaveInProgress = false;
                refreshDeferred = _requestedRevision > _appliedRevision;
            }
            _logger?.LogError(exception,
                "PermissionSaveResponseFailed CommunityId={CommunityId} ScopeId={ScopeId} ScopeType={ScopeType}",
                communityId, scopeId, scopeType);
            if (refreshDeferred) QueueRealtimeRefresh(_requestedRevision, _managementRefreshRequested);
            throw;
        }
    }

    public async Task RemovePermissionOverwriteAsync(PermissionOverwriteScopeType scopeType, Guid scopeId,
        PermissionOverwriteTargetType targetType, Guid? targetId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.RemovePermissionOverwriteAsync(RequireCommunity(), scopeType, scopeId,
            new(targetType, targetId), cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task SyncChannelPermissionsAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        await _nodeSession.AuthorizedClient.SyncChannelPermissionsAsync(RequireCommunity(), channelId, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public void MarkChannelRead(Guid channelId)
    {
        var index = _channels.FindIndex(value => value.Id == channelId);
        if (index >= 0) _channels[index] = _channels[index] with { UnreadCount = 0, MentionCount = 0 };
    }

    public void MarkChannelUnread(Guid channelId)
    {
        var index = _channels.FindIndex(value => value.Id == channelId);
        if (index >= 0) _channels[index] = _channels[index] with { UnreadCount = Math.Max(1, _channels[index].UnreadCount + 1) };
    }

    public CommunityChannelDto? FirstOrderedChannel()
    {
        return FirstIn(null);

        CommunityChannelDto? FirstIn(Guid? categoryId)
        {
            var items = _categories.Where(value => value.ParentCategoryId == categoryId)
                .Select(value => (value.Position, Kind: 1, Category: (CommunityCategoryDto?)value,
                    Channel: (CommunityChannelDto?)null, value.Id))
                .Concat(_channels.Where(value => value.CategoryId == categoryId)
                    .Select(value => (value.Position, Kind: 0, Category: (CommunityCategoryDto?)null,
                        Channel: (CommunityChannelDto?)value, value.Id)))
                .OrderBy(value => value.Position).ThenBy(value => value.Kind).ThenBy(value => value.Id);
            foreach (var item in items)
            {
                if (item.Channel is { Kind: CommunityChannelKind.Text or CommunityChannelKind.Forum } direct) return direct;
                if (item.Category is { } child && FirstIn(child.Id) is { } nested) return nested;
            }
            return null;
        }
    }

    public void ApplyPresence(PresenceChangedEvent change)
    {
        if (Management is null) return;
        var members = Management.Members.Select(value => value.AccountId == change.AccountId
            ? value with { Presence = change.Presence }
            : value).ToArray();
        Management = Management with { Members = members };
    }

    public void Clear()
    {
        CommunityId = null;
        CanManage = false;
        EffectivePermissions = CommunityPermission.None;
        IsOwner = false;
        CanManagePermissions = false;
        Management = null;
        _categories.Clear();
        _channels.Clear();
        lock (_realtimeGate)
        {
            _requestedRevision = 0;
            _appliedRevision = 0;
            _managementRefreshRequested = false;
            _generation++;
        }
    }

    private void OnCommunityChanged(CommunityStateChangedEvent change)
    {
        if (CommunityId != change.CommunityId) return;
        if (change.Change == "expressions-updated") return;
        lock (_realtimeGate)
        {
            _logger?.LogDebug(
                "PermissionRealtimeReceived CommunityId={CommunityId} Mutation={Mutation} CurrentRevision={CurrentRevision} IncomingRevision={IncomingRevision} SaveInProgress={SaveInProgress}",
                change.CommunityId, change.Change, _appliedRevision, change.Revision, _permissionSaveInProgress);
            if (_permissionSaveInProgress && change.Change == "permissions-updated")
            {
                _requestedRevision = Math.Max(_requestedRevision, change.Revision);
                _managementRefreshRequested = true;
                return;
            }
        }
        QueueRealtimeRefresh(change.Revision, RequiresManagementRefresh(change.Change));
    }

    private void OnRealtimeReconnected()
    {
        if (CommunityId is not null) QueueRealtimeRefresh(0, refreshManagement: true);
    }

    private void QueueRealtimeRefresh(long revision, bool refreshManagement)
    {
        lock (_realtimeGate)
        {
            if (revision > 0 && revision <= _appliedRevision) return;
            var requested = revision > 0 ? revision : Math.Max(_requestedRevision, _appliedRevision) + 1;
            _requestedRevision = Math.Max(_requestedRevision, requested);
            _managementRefreshRequested |= refreshManagement;
            if (_realtimeRefreshRunning) return;
            _realtimeRefreshRunning = true;
        }
        _ = RefreshRealtimeLoopAsync();
    }

    private async Task RefreshRealtimeLoopAsync()
    {
        while (true)
        {
            long targetRevision;
            long generation;
            Guid communityId;
            bool reloadManagement;
            lock (_realtimeGate)
            {
                if (CommunityId is not { } activeCommunityId)
                {
                    _realtimeRefreshRunning = false;
                    return;
                }
                communityId = activeCommunityId;
                generation = _generation;
                targetRevision = _requestedRevision;
                reloadManagement = Management is not null && _managementRefreshRequested;
                _managementRefreshRequested = false;
            }

            var refreshed = false;
            try
            {
                _logger?.LogDebug(
                    "CommunityReloadStarted CommunityId={CommunityId} CurrentRevision={CurrentRevision} IncomingRevision={IncomingRevision}",
                    communityId, AppliedRevision, targetRevision);
                var structure = await _nodeSession.AuthorizedClient.GetCommunityStructureAsync(communityId);
                var management = reloadManagement
                    ? await _nodeSession.AuthorizedClient.GetCommunityManagementAsync(communityId)
                    : null;
                lock (_realtimeGate)
                {
                    if (generation != _generation || CommunityId != communityId)
                    {
                        _realtimeRefreshRunning = false;
                        return;
                    }
                    CanManage = structure.CanManage;
                    EffectivePermissions = structure.EffectivePermissions;
                    IsOwner = structure.IsOwner;
                    CanManagePermissions = structure.CanManagePermissions;
                    Replace(structure);
                    if (management is not null)
                    {
                        Management = management;
                        EffectivePermissions = management.Access.Permissions;
                        IsOwner = management.Access.IsOwner;
                        CanManage = HasPermission(CommunityPermission.ManageChannels);
                        CanManagePermissions = HasPermission(CommunityPermission.ManagePermissions);
                    }
                }
                refreshed = true;
                Changed?.Invoke();
                _logger?.LogDebug(
                    "CommunityReloadSucceeded CommunityId={CommunityId} IncomingRevision={IncomingRevision}",
                    communityId, targetRevision);
            }
            catch (Exception exception)
            {
                _logger?.LogError(exception,
                    "CommunityReloadFailed CommunityId={CommunityId} IncomingRevision={IncomingRevision}",
                    communityId, targetRevision);
                RealtimeRefreshFailed?.Invoke(exception);
            }

            lock (_realtimeGate)
            {
                if (!refreshed)
                {
                    _managementRefreshRequested |= reloadManagement;
                    _realtimeRefreshRunning = false;
                    return;
                }
                _appliedRevision = Math.Max(_appliedRevision, targetRevision);
                if (CommunityId == communityId && _requestedRevision > _appliedRevision) continue;
                _realtimeRefreshRunning = false;
                return;
            }
        }
    }

    public void Dispose()
    {
        _nodeSession.CommunityChanged -= OnCommunityChanged;
        _nodeSession.RealtimeReconnected -= OnRealtimeReconnected;
    }

    private static bool RequiresManagementRefresh(string change) =>
        change.StartsWith("role", StringComparison.Ordinal) ||
        change.StartsWith("member", StringComparison.Ordinal) ||
        change.StartsWith("permission", StringComparison.Ordinal) ||
        change.StartsWith("invite", StringComparison.Ordinal) ||
        change is "overview" or "member-profile-updated";

    private async Task ReloadAsync(CancellationToken cancellationToken) => await LoadAsync(RequireCommunity(), cancellationToken);
    private Guid RequireCommunity() => CommunityId ?? throw new InvalidOperationException("Select a Server first.");
    private void Replace(CommunityStructureDto structure) { _categories.Clear(); _categories.AddRange(structure.Categories); _channels.Clear(); _channels.AddRange(structure.Channels); Sort(); }
    private void ReplaceCategory(CommunityCategoryDto value) { _categories.RemoveAll(item => item.Id == value.Id); _categories.Add(value); Sort(); }
    private void ReplaceChannel(CommunityChannelDto value) { _channels.RemoveAll(item => item.Id == value.Id); _channels.Add(value); Sort(); }
    private void Sort() { _categories.Sort((a,b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : string.Compare(a.Name,b.Name,StringComparison.OrdinalIgnoreCase)); _channels.Sort((a,b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : string.Compare(a.Name,b.Name,StringComparison.OrdinalIgnoreCase)); }
}

public interface ICategoryCollapseStore
{
    Task<IReadOnlySet<Guid>> LoadAsync(Guid accountId, Guid communityId, CancellationToken cancellationToken = default);
    Task SaveAsync(Guid accountId, Guid communityId, IReadOnlySet<Guid> collapsed, CancellationToken cancellationToken = default);
}

public interface ILastCommunityChannelStore
{
    Task<Guid?> LoadAsync(Guid accountId, Guid communityId, CancellationToken cancellationToken = default);
    Task SaveAsync(Guid accountId, Guid communityId, Guid channelId, CancellationToken cancellationToken = default);
}
