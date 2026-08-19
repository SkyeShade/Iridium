namespace Iridium.Protocol;

public static class MessageHistoryDefaults
{
    public const int PageSize = 50;
    public const int MaximumPageSize = 100;
    public const int SearchPageSize = 25;
    public const int AroundHalfWindow = 25;
}

public sealed record MessageHistoryPage<TMessage>(
    IReadOnlyList<TMessage> Messages,
    string? OlderCursor,
    bool HasOlder,
    bool IsAroundWindow = false,
    Guid? TargetMessageId = null);

public sealed record MessageSearchResultDto(
    Guid MessageId,
    Guid? CommunityId,
    Guid? ChannelId,
    Guid? ConversationId,
    string? ChannelName,
    MessageAuthorDto Author,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record MessageSearchPageDto(
    IReadOnlyList<MessageSearchResultDto> Results,
    string? OlderCursor,
    bool HasMore);

public enum MessageSearchContentType
{
    Link,
    File,
    Image,
    Video,
    Embed
}

public enum MessageSearchSort
{
    Newest,
    Oldest
}

public enum MessageAuthorType
{
    User,
    Bot,
    Webhook
}

public sealed record MessageSearchQueryDto(
    string? Text,
    Guid? FromAccountId,
    Guid? ChannelId,
    Guid? MentionedAccountId,
    IReadOnlyList<MessageSearchContentType> HasTypes,
    DateTimeOffset? BeforeUtc,
    DateTimeOffset? AfterUtc,
    DateTimeOffset? DuringStartUtc,
    DateTimeOffset? DuringEndUtc,
    MessageAuthorType AuthorType = MessageAuthorType.User,
    MessageSearchSort Sort = MessageSearchSort.Newest);

public sealed record MessageSearchRequest(
    MessageSearchQueryDto Query,
    string? Cursor = null,
    int Limit = MessageHistoryDefaults.SearchPageSize);

public enum MessageWindowMode
{
    Latest,
    Historical,
    SearchTarget
}
