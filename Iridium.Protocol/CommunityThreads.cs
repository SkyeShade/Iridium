namespace Iridium.Protocol;

public enum ThreadAutoArchiveDuration
{
    OneHour = 60,
    OneDay = 1440,
    ThreeDays = 4320,
    SevenDays = 10080
}

public enum ThreadNotificationLevel { AllMessages, MentionsOnly, Nothing, Muted }
public enum CommunityThreadListKind { Active, Joined, Archived }

public static class CommunityThreadLimits
{
    public const int MinimumNameLength = 1;
    public const int MaximumNameLength = 100;
    public const int MaximumActiveThreadsPerChannel = 500;
    public const int MaximumCreatesPerMinute = 5;
    public static readonly IReadOnlySet<int> SlowmodeSeconds = new HashSet<int>
        { 0, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 21600 };
}

public sealed record CommunityThreadDto(
    Guid Id,
    Guid CommunityId,
    Guid ParentChannelId,
    Guid DiscussionChannelId,
    string Name,
    Guid OwnerAccountId,
    Guid? CreatedFromMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    bool IsArchived,
    bool IsLocked,
    bool IsPrivate,
    ThreadAutoArchiveDuration AutoArchiveDuration,
    int SlowmodeSeconds,
    DateTimeOffset LastActivityAt,
    int ReplyCount,
    bool IsJoined,
    ThreadNotificationLevel? NotificationLevel,
    DateTimeOffset? JoinedAt,
    int UnreadCount,
    int MentionCount,
    bool CanManage,
    bool CanEditOwn);

public sealed record CommunityThreadPageDto(IReadOnlyList<CommunityThreadDto> Threads, bool HasMore);
public sealed record ThreadMemberDto(Guid AccountId, string DisplayName, DateTimeOffset JoinedAt);
public sealed record CreateCommunityThreadRequest(
    string Name,
    bool IsPrivate = false,
    Guid? CreatedFromMessageId = null,
    ThreadAutoArchiveDuration AutoArchiveDuration = ThreadAutoArchiveDuration.ThreeDays);
public sealed record UpdateCommunityThreadRequest(
    string? Name = null,
    bool? IsArchived = null,
    bool? IsLocked = null,
    ThreadAutoArchiveDuration? AutoArchiveDuration = null,
    int? SlowmodeSeconds = null);
public sealed record UpdateThreadNotificationRequest(ThreadNotificationLevel Level);

public static class CommunityThreadHubContract
{
    public const string Created = "ThreadCreated";
    public const string Updated = "ThreadUpdated";
    public const string Archived = "ThreadArchived";
    public const string Unarchived = "ThreadUnarchived";
    public const string Deleted = "ThreadDeleted";
    public const string MembershipChanged = "ThreadMembershipChanged";
    public const string ActivityChanged = "ThreadActivityChanged";
}

public sealed record CommunityThreadChangedEvent(
    Guid CommunityId, Guid ParentChannelId, Guid ThreadId, bool IsDeleted = false);
public sealed record CommunityThreadMembershipChangedEvent(
    Guid CommunityId, Guid ParentChannelId, Guid ThreadId, Guid AccountId, bool IsJoined);
public sealed record CommunityThreadActivityChangedEvent(
    Guid CommunityId, Guid ParentChannelId, Guid ThreadId, int ReplyCount, DateTimeOffset LastActivityAt,
    Guid ActorAccountId);
