using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class CommunityForumSession(
    NodeSession session,
    RealtimeConnectionService realtime,
    ICommunityForumPostCache cache,
    ILogger<CommunityForumSession> logger) : IAsyncDisposable
{
    private readonly List<CommunityForumPostDto> _posts = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IDisposable> _registrations = [];
    private IDisposable? _recoveryRegistration;
    private bool _initialized;
    private HubConnection? _boundConnection;
    private bool _disposed;
    private string _search = string.Empty;
    private IReadOnlyList<Guid> _tagFilter = [];

    public Guid? CommunityId { get; private set; }
    public Guid? ChannelId { get; private set; }
    public IReadOnlyList<CommunityForumPostDto> Posts => _posts;
    public IReadOnlyList<CommunityForumTagDto> Tags { get; private set; } = [];
    public int? NextOffset { get; private set; }
    public bool IsLoading { get; private set; }
    public Guid? LastDeletedPostId { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public async Task LoadAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default)
    {
        await EnsureRealtimeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (CommunityId != communityId || ChannelId != channelId)
            {
                _search = string.Empty;
                _tagFilter = [];
            }
            CommunityId = communityId;
            ChannelId = channelId;
            IsLoading = true;
            Error = null;
            var scope = CacheScope(communityId, channelId);
            CommunityForumPostPageDto? cached = null;
            try { cached = await cache.LoadAsync(scope, cancellationToken); }
            catch (Exception exception) { logger.LogDebug(exception, "Could not read cached Forum post metadata."); }
            if (cached is not null && CommunityId == communityId && ChannelId == channelId)
            {
                _posts.Clear();
                _posts.AddRange(cached.Posts);
                NextOffset = cached.NextOffset;
                Notify();
            }
            Notify();
            var tagsTask = session.AuthorizedClient.GetForumTagsAsync(communityId, channelId, cancellationToken);
            var page = await session.AuthorizedClient.QueryForumPostsAsync(communityId, channelId, _search,
                _tagFilter, cancellationToken: cancellationToken);
            Tags = await tagsTask;
            if (CommunityId != communityId || ChannelId != channelId) return;
            _posts.Clear();
            _posts.AddRange(page.Posts);
            NextOffset = page.NextOffset;
            await cache.SaveAsync(scope, page, cancellationToken);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            logger.LogWarning(exception, "Could not load Forum posts for {CommunityId}/{ChannelId}.", communityId, channelId);
        }
        finally
        {
            IsLoading = false;
            _gate.Release();
            Notify();
        }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (CommunityId is not { } communityId || ChannelId is not { } channelId || NextOffset is not { } offset) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;
            Notify();
            var page = await session.AuthorizedClient.QueryForumPostsAsync(communityId, channelId, _search,
                _tagFilter, offset, cancellationToken: cancellationToken);
            foreach (var post in page.Posts) Upsert(post);
            NextOffset = page.NextOffset;
            PersistCacheSafely();
        }
        finally
        {
            IsLoading = false;
            _gate.Release();
            Notify();
        }
    }

    public async Task<CommunityForumPostDto> CreateAsync(string title, string content,
        IReadOnlyList<CommunityMentionInput>? mentions, IReadOnlyList<AttachmentDto>? attachments,
        Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>? uploadAttachments,
        IReadOnlyList<Guid>? tagIds = null, CancellationToken cancellationToken = default)
    {
        var communityId = CommunityId ?? throw new InvalidOperationException("Open a Forum first.");
        var channelId = ChannelId ?? throw new InvalidOperationException("Open a Forum first.");
        if (uploadAttachments is not null) attachments = await uploadAttachments(cancellationToken);
        var post = await session.AuthorizedClient.CreateForumPostAsync(communityId, channelId,
            new(title, new(content, null, mentions, Guid.NewGuid(), attachments?.Select(value => value.Id).ToArray()),
                tagIds),
            cancellationToken);
        Upsert(post);
        PersistCacheSafely();
        Notify();
        return post;
    }

    public async Task<CommunityForumPostDto> UpdateAsync(Guid postId, UpdateCommunityForumPostRequest request,
        CancellationToken cancellationToken = default)
    {
        var post = await session.AuthorizedClient.UpdateForumPostAsync(CommunityId!.Value, ChannelId!.Value,
            postId, request, cancellationToken);
        Upsert(post);
        PersistCacheSafely();
        Notify();
        return post;
    }

    public async Task DeleteAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        await session.AuthorizedClient.DeleteForumPostAsync(CommunityId!.Value, ChannelId!.Value, postId,
            cancellationToken);
        _posts.RemoveAll(value => value.Id == postId);
        PersistCacheSafely();
        Notify();
    }

    public async Task<CommunityForumPostDto> UpdateTagsAsync(Guid postId, IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        var post = await session.AuthorizedClient.UpdateForumPostTagsAsync(CommunityId!.Value, ChannelId!.Value,
            postId, tagIds, cancellationToken);
        Upsert(post);
        PersistCacheSafely();
        Notify();
        return post;
    }

    public async Task ApplyFilterAsync(string? search, IReadOnlyCollection<Guid>? tagIds,
        CancellationToken cancellationToken = default)
    {
        if (CommunityId is not { } communityId || ChannelId is not { } channelId) return;
        _search = search?.Trim() ?? string.Empty;
        _tagFilter = tagIds?.Distinct().ToArray() ?? [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;
            Notify();
            var page = await session.AuthorizedClient.QueryForumPostsAsync(communityId, channelId, _search,
                _tagFilter, cancellationToken: cancellationToken);
            _posts.Clear();
            _posts.AddRange(page.Posts);
            NextOffset = page.NextOffset;
        }
        finally { IsLoading = false; _gate.Release(); Notify(); }
    }

    public void MarkRead(Guid postId)
    {
        var index = _posts.FindIndex(value => value.Id == postId);
        if (index < 0 || _posts[index].UnreadCount == 0) return;
        _posts[index] = _posts[index] with { UnreadCount = 0 };
        PersistCacheSafely();
        Notify();
    }

    private async Task EnsureRealtimeAsync(CancellationToken cancellationToken)
    {
        var connection = await realtime.EnsureConnectedAsync("forum-session", cancellationToken);
        if (ReferenceEquals(connection, _boundConnection)) return;
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        _registrations.Add(connection.On<CommunityForumPostChangedEvent>(CommunityForumHubContract.PostChanged,
            ReceiveChange));
        _registrations.Add(connection.On<CommunityForumTagsChangedEvent>(CommunityForumHubContract.TagsChanged,
            ReceiveTagsChange));
        _boundConnection = connection;
        if (!_initialized) _recoveryRegistration = realtime.RegisterRecoveryHandler("forum-post-list", async (_, ct) =>
        {
            if (CommunityId is { } communityId && ChannelId is { } channelId)
                await LoadAsync(communityId, channelId, ct);
        });
        _initialized = true;
    }

    private void ReceiveChange(CommunityForumPostChangedEvent change)
    {
        if (change.CommunityId != CommunityId || change.ForumChannelId != ChannelId) return;
        LastDeletedPostId = change.Change == "deleted" ? change.PostId : null;
        if (change.Post is null || change.Change == "deleted") _posts.RemoveAll(value => value.Id == change.PostId);
        else
        {
            var existing = _posts.FirstOrDefault(value => value.Id == change.PostId);
            var post = change.Change is "activity" or "created" && change.ActorAccountId != session.Account?.Id
                ? change.Post with { UnreadCount = Math.Max(1, (existing?.UnreadCount ?? 0) + 1) }
                : change.Post with { UnreadCount = existing?.UnreadCount ?? change.Post.UnreadCount };
            if (MatchesCurrentFilter(post)) Upsert(post);
            else _posts.RemoveAll(value => value.Id == post.Id);
        }
        PersistCacheSafely();
        Notify();
        LastDeletedPostId = null;
    }

    private void ReceiveTagsChange(CommunityForumTagsChangedEvent change)
    {
        if (change.CommunityId != CommunityId || change.ForumChannelId != ChannelId) return;
        Tags = change.Tags;
        var definitions = change.Tags.ToDictionary(value => value.Id);
        var validFilter = _tagFilter.Where(definitions.ContainsKey).ToArray();
        var filterChanged = validFilter.Length != _tagFilter.Count;
        _tagFilter = validFilter;
        for (var index = 0; index < _posts.Count; index++)
            _posts[index] = _posts[index] with { Tags = (_posts[index].Tags ?? [])
                .Where(value => definitions.ContainsKey(value.Id)).Select(value => definitions[value.Id])
                .OrderBy(value => value.SortOrder).ToArray() };
        PersistCacheSafely();
        Notify();
        if (filterChanged) _ = ApplyFilterAsync(_search, _tagFilter);
    }

    private bool MatchesCurrentFilter(CommunityForumPostDto post)
    {
        if (_tagFilter.Count > 0 && !(post.Tags ?? []).Any(value => _tagFilter.Contains(value.Id))) return false;
        return CommunityForumPostSearch.Filter([post], _search, _tagFilter).Count == 1;
    }

    private void Upsert(CommunityForumPostDto post)
    {
        _posts.RemoveAll(value => value.Id == post.Id);
        _posts.Add(post);
        _posts.Sort((left, right) =>
        {
            var pinned = right.IsPinned.CompareTo(left.IsPinned);
            return pinned != 0 ? pinned : right.LastActivityAt.CompareTo(left.LastActivityAt);
        });
    }

    private CommunityForumPostCacheScope CacheScope(Guid communityId, Guid channelId)
    {
        var node = session.SelectedNode ?? throw new InvalidOperationException("Select a Node first.");
        var account = session.Account ?? throw new InvalidOperationException("Sign in first.");
        return new(MessageHistoryCacheScope.NormalizeNode(new Uri(node.Address)), account.Id, communityId, channelId);
    }

    private void PersistCacheSafely()
    {
        if (CommunityId is not { } communityId || ChannelId is not { } channelId) return;
        _ = SaveCacheSafelyAsync(CacheScope(communityId, channelId),
            new(_posts.Take(50).ToArray(), NextOffset));
    }

    private async Task SaveCacheSafelyAsync(CommunityForumPostCacheScope scope, CommunityForumPostPageDto page)
    {
        try { await cache.SaveAsync(scope, page); }
        catch (Exception exception) { logger.LogDebug(exception, "Could not cache Forum post metadata."); }
    }

    private void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        foreach (var registration in _registrations) registration.Dispose();
        _recoveryRegistration?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
