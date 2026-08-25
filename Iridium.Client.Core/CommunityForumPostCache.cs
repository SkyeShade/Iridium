using Iridium.Protocol;

namespace Iridium.Client.Core;

public readonly record struct CommunityForumPostCacheScope(
    string NodeKey, Guid AccountId, Guid CommunityId, Guid ForumChannelId)
{
    public string StorageKey =>
        $"iridium.forum-posts.v1:{NodeKey}|account:{AccountId:N}|community:{CommunityId:N}|forum:{ForumChannelId:N}";
}

public interface ICommunityForumPostCache
{
    Task<CommunityForumPostPageDto?> LoadAsync(CommunityForumPostCacheScope scope,
        CancellationToken cancellationToken = default);
    Task SaveAsync(CommunityForumPostCacheScope scope, CommunityForumPostPageDto page,
        CancellationToken cancellationToken = default);
}

public sealed class NullCommunityForumPostCache : ICommunityForumPostCache
{
    public Task<CommunityForumPostPageDto?> LoadAsync(CommunityForumPostCacheScope scope,
        CancellationToken cancellationToken = default) => Task.FromResult<CommunityForumPostPageDto?>(null);
    public Task SaveAsync(CommunityForumPostCacheScope scope, CommunityForumPostPageDto page,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
