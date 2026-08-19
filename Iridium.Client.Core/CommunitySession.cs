using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed class CommunitySession(NodeSession nodeSession)
{
    private readonly List<CommunityCategoryDto> _categories = [];
    private readonly List<CommunityChannelDto> _channels = [];

    public Guid? CommunityId { get; private set; }
    public bool CanManage { get; private set; }
    public CommunityPermission EffectivePermissions { get; private set; }
    public bool IsOwner { get; private set; }
    public CommunityManagementDto? Management { get; private set; }
    public IReadOnlyList<CommunityCategoryDto> Categories => _categories;
    public IReadOnlyList<CommunityChannelDto> Channels => _channels;

    public async Task LoadAsync(Guid communityId, CancellationToken cancellationToken = default)
    {
        var structure = await nodeSession.AuthorizedClient.GetCommunityStructureAsync(communityId, cancellationToken);
        CommunityId = communityId;
        CanManage = structure.CanManage;
        EffectivePermissions = structure.EffectivePermissions;
        IsOwner = structure.IsOwner;
        Replace(structure);
    }

    public bool HasPermission(CommunityPermission permission) =>
        IsOwner || (EffectivePermissions & CommunityPermission.Administrator) != 0 ||
        (EffectivePermissions & permission) == permission;

    public async Task<CommunityManagementDto> LoadManagementAsync(CancellationToken cancellationToken = default)
    {
        Management = await nodeSession.AuthorizedClient.GetCommunityManagementAsync(RequireCommunity(), cancellationToken);
        EffectivePermissions = Management.Access.Permissions;
        IsOwner = Management.Access.IsOwner;
        CanManage = HasPermission(CommunityPermission.ManageChannels);
        return Management;
    }

    public async Task<CommunityDto> UpdateCommunityAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        var updated = await nodeSession.AuthorizedClient.UpdateCommunityAsync(
            RequireCommunity(), new UpdateCommunityRequest(name, description), cancellationToken);
        await LoadManagementAsync(cancellationToken);
        return updated;
    }

    public async Task CreateRoleAsync(string name, CommunityPermission permissions, string? color, bool displaySeparately = false, bool isMentionable = false, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.CreateCommunityRoleAsync(RequireCommunity(), new(name, permissions, color, displaySeparately, isMentionable), cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task UpdateRoleAsync(Guid roleId, string name, CommunityPermission permissions, string? color, bool displaySeparately = false, bool isMentionable = false, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.UpdateCommunityRoleAsync(RequireCommunity(), roleId, new(name, permissions, color, displaySeparately, isMentionable), cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.DeleteCommunityRoleAsync(RequireCommunity(), roleId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task MoveRoleAsync(Guid roleId, int position, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.MoveCommunityRoleAsync(RequireCommunity(), roleId, position, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task SetMemberRolesAsync(Guid accountId, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.SetCommunityMemberRolesAsync(RequireCommunity(), accountId, roleIds, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task KickMemberAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.KickCommunityMemberAsync(RequireCommunity(), accountId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task BanMemberAsync(Guid accountId, string? reason, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.BanCommunityMemberAsync(RequireCommunity(), accountId, reason, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task UnbanMemberAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.UnbanCommunityMemberAsync(RequireCommunity(), accountId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task<CommunityInviteDto> CreateInviteAsync(DateTimeOffset? expiresAt, int? maxUses, CancellationToken cancellationToken = default)
    {
        var invite = await nodeSession.AuthorizedClient.CreateCommunityInviteAsync(
            RequireCommunity(), new(expiresAt, maxUses), cancellationToken);
        await LoadManagementAsync(cancellationToken);
        return invite;
    }

    public async Task RevokeInviteAsync(Guid inviteId, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.RevokeCommunityInviteAsync(RequireCommunity(), inviteId, cancellationToken);
        await LoadManagementAsync(cancellationToken);
    }

    public async Task<CommunityCategoryDto> CreateCategoryAsync(string name, CancellationToken cancellationToken = default)
    {
        var created = await nodeSession.AuthorizedClient.CreateCategoryAsync(RequireCommunity(), name, cancellationToken);
        _categories.Add(created); Sort(); return created;
    }

    public async Task UpdateCategoryAsync(Guid categoryId, string name, CancellationToken cancellationToken = default)
    {
        var updated = await nodeSession.AuthorizedClient.UpdateCategoryAsync(RequireCommunity(), categoryId, name, cancellationToken);
        ReplaceCategory(updated);
    }

    public async Task MoveCategoryAsync(Guid categoryId, int position, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.MoveCategoryAsync(RequireCommunity(), categoryId, position, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.DeleteCategoryAsync(RequireCommunity(), categoryId, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task<CommunityChannelDto> CreateChannelAsync(string name, Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var created = await nodeSession.AuthorizedClient.CreateChannelAsync(RequireCommunity(), name, categoryId, cancellationToken);
        _channels.Add(created); Sort(); return created;
    }

    public async Task UpdateChannelAsync(Guid channelId, string name, Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var updated = await nodeSession.AuthorizedClient.UpdateChannelAsync(RequireCommunity(), channelId, name, categoryId, cancellationToken);
        ReplaceChannel(updated);
    }

    public async Task MoveChannelAsync(Guid channelId, Guid? categoryId, int position, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.MoveChannelAsync(RequireCommunity(), channelId, categoryId, position, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task DeleteChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        await nodeSession.AuthorizedClient.DeleteChannelAsync(RequireCommunity(), channelId, cancellationToken);
        _channels.RemoveAll(value => value.Id == channelId);
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
        var top = _categories.Select(value => (value.Position, IsCategory: true, value.Id))
            .Concat(_channels.Where(value => value.CategoryId is null).Select(value => (value.Position, IsCategory: false, value.Id)))
            .OrderBy(value => value.Position).ThenBy(value => value.IsCategory ? 1 : 0);
        foreach (var item in top)
        {
            if (!item.IsCategory) return _channels.First(value => value.Id == item.Id);
            var nested = _channels.Where(value => value.CategoryId == item.Id)
                .OrderBy(value => value.Position).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (nested is not null) return nested;
        }
        return null;
    }

    public void ApplyPresence(PresenceChangedEvent change)
    {
        if (Management is null) return;
        var members = Management.Members.Select(value => value.AccountId == change.AccountId
            ? value with { Presence = change.Presence }
            : value).ToArray();
        Management = Management with { Members = members };
    }

    public void Clear() { CommunityId = null; CanManage = false; EffectivePermissions = CommunityPermission.None; IsOwner = false; Management = null; _categories.Clear(); _channels.Clear(); }

    private async Task ReloadAsync(CancellationToken cancellationToken) => await LoadAsync(RequireCommunity(), cancellationToken);
    private Guid RequireCommunity() => CommunityId ?? throw new InvalidOperationException("Select a Community first.");
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
