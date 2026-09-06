namespace Iridium.Client.Core;

public readonly record struct ForumPostCollapseScope(string NodeAuthority, Guid AccountId, Guid ForumId)
{
    public string StorageKey =>
        $"iridium.forum-post-collapse.v1:{Uri.EscapeDataString(NodeAuthority)}:{AccountId:N}:{ForumId:N}";
}

public interface IForumPostCollapseStore
{
    Task<bool?> LoadAsync(ForumPostCollapseScope scope, CancellationToken cancellationToken = default);
    Task SaveAsync(ForumPostCollapseScope scope, bool collapsed, CancellationToken cancellationToken = default);
}
