using System.Text;

namespace Iridium.Protocol;

public sealed record ChatMessageDto(
    MessageId Id,
    ConversationId ConversationId,
    UserId SenderId,
    string Content,
    DateTimeOffset SentAt);

public sealed record SendChatMessage(ConversationId ConversationId, string Content);
public sealed record ChatMessageReceived(ChatMessageDto Message);
public static class NodeLimitDefaults
{
    public const long MaxAttachmentBytes = 200L * 1024 * 1024;
}
public sealed record ServerInfoDto(
    string Name,
    string Motd,
    int ProtocolVersion,
    int OnlineUsers,
    int? MaximumUsers,
    string? ServerIconUrl,
    long MaxAttachmentBytes = NodeLimitDefaults.MaxAttachmentBytes,
    int MaxAttachmentsPerMessage = 10,
    int MaxMessageCharacters = 10_000,
    bool VoiceEnabled = false,
    bool ScreenShareEnabled = false);

public static class MessageText
{
    // A character is one Unicode scalar value (Rune) on both client and server.
    public static int CountCharacters(string? value) => value is null ? 0 :
        CommunityEmojiNames.ToCharacterCountingText(value).EnumerateRunes().Count();

    public static bool AllowsMentionAt(string value, int position)
    {
        if (position < 0 || position >= value.Length) return false;
        var cursor = 0;
        while (cursor < value.Length)
        {
            var markerLength = value.AsSpan(cursor).StartsWith("```") ? 3 : value[cursor] == '`' ? 1 : 0;
            if (markerLength == 0) { cursor++; continue; }
            var closing = value.IndexOf(new string('`', markerLength), cursor + markerLength,
                StringComparison.Ordinal);
            if (closing < 0) return true;
            if (position >= cursor && position < closing + markerLength) return false;
            cursor = closing + markerLength;
        }
        return true;
    }
}

public sealed record AttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string DownloadUrl,
    int? Width = null,
    int? Height = null,
    string? AverageColor = null,
    string? LocalPreviewUrl = null,
    bool IsSpoiler = false,
    string? PreviewDownloadUrl = null,
    string? PreviewContentType = null,
    long? PreviewSizeBytes = null)
{
    public string OriginalContentType => ContentType;
    public long OriginalSizeBytes => SizeBytes;
    public string OriginalDownloadUrl => DownloadUrl;
}

public sealed record AttachmentUploadDto(Guid Id, string OriginalFileName, string ContentType, long SizeBytes,
    int? Width = null, int? Height = null, string? AverageColor = null, bool IsSpoiler = false,
    string? PreviewContentType = null, long? PreviewSizeBytes = null)
{
    public string OriginalContentType => ContentType;
    public long OriginalSizeBytes => SizeBytes;
}

public sealed record AttachmentPlaybackAccessDto(string Url, DateTimeOffset ExpiresAt);

public sealed record RegisterAccountRequest(string Username, string DisplayName, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record NodeAccountDto(
    Guid Id,
    string Username,
    string DisplayName,
    string? Pronouns,
    string? Description,
    UserPresence PreferredPresence,
    DateTimeOffset CreatedAt,
    Guid? ActiveAvatarPresetId = null,
    long AvatarRevision = 0,
    Guid? ActiveBannerPresetId = null,
    long BannerRevision = 0,
    Guid? BaseAvatarPresetId = null);
public sealed record AuthenticationResultDto(string AccessToken, NodeAccountDto Account);
public sealed record UpdateProfileRequest(string DisplayName, string? Pronouns, string? Description);
public sealed record CommunityDto(Guid Id, string Name, string? Description, Guid OwnerAccountId, DateTimeOffset CreatedAt,
    bool HasUnread = false, int MentionCount = 0, Guid? ActiveAvatarPresetId = null, long AvatarRevision = 0,
    Guid? ActiveBannerPresetId = null, long BannerRevision = 0, string? AvatarUrl = null, string? BannerUrl = null,
    double AvatarCropX = 0, double AvatarCropY = 0, double AvatarZoom = 1, int AvatarWidth = 0, int AvatarHeight = 0,
    double BannerCropX = 0, double BannerCropY = 0, double BannerZoom = 1, int BannerWidth = 0, int BannerHeight = 0,
    bool BannerIsProcessed = false);
public sealed record CreateCommunityRequest(string Name, string? Description);

public enum FriendshipStatus
{
    Pending,
    Accepted
}

public sealed record FriendDto(
    Guid FriendshipId,
    Guid AccountId,
    string Username,
    string DisplayName,
    string? Pronouns,
    string? Description,
    FriendshipStatus Status,
    bool IsOutgoing,
    PublicPresence Presence)
{
    public bool IsOnline => Presence != PublicPresence.Offline;
}

public enum ProfileRelationshipStatus
{
    None,
    Self,
    OutgoingPending,
    IncomingPending,
    Friends
}

public sealed record ResolvedProfileDto(
    Guid AccountId,
    string Username,
    string DisplayName,
    string? Pronouns,
    string? Description,
    ProfileRelationshipStatus Relationship,
    Guid? FriendshipId,
    PublicPresence Presence,
    bool IsBlockedByCurrentAccount = false)
{
    public bool IsOnline => Presence != PublicPresence.Offline;
}

public sealed record FriendSearchResultDto(
    Guid AccountId,
    string Username,
    string DisplayName,
    ProfileRelationshipStatus Relationship,
    Guid? FriendshipId,
    PublicPresence Presence);

public sealed record SendFriendRequest(string Username);
public sealed record ProfileBlockChange(Guid AccountId, bool IsBlocked);

public sealed record CommunityCategoryDto(Guid Id, Guid CommunityId, string Name, int Position, Guid? ParentCategoryId,
    bool HasPermissionOverwrites = false,
    CommunityPermission EffectivePermissions = CommunityPermission.None,
    bool IsPrivate = false);
public enum CommunityChannelKind
{
    Text = 0,
    Voice = 1,
    Forum = 2
}

public sealed record CommunityChannelDto(Guid Id, Guid CommunityId, Guid? CategoryId, string Name, int Position,
    DateTimeOffset CreatedAt, int UnreadCount = 0, int MentionCount = 0,
    CommunityChannelKind Kind = CommunityChannelKind.Text,
    CommunityPermission EffectivePermissions = CommunityPermission.None,
    bool PermissionsSyncedToCategory = false,
    bool IsPrivate = false);
public sealed record CommunityStructureDto(
    Guid CommunityId,
    bool CanManage,
    IReadOnlyList<CommunityCategoryDto> Categories,
    IReadOnlyList<CommunityChannelDto> Channels,
    CommunityPermission EffectivePermissions = CommunityPermission.None,
    bool IsOwner = false,
    bool CanManagePermissions = false);
public sealed record CreateCategoryRequest(string Name, Guid? ParentCategoryId = null);
public sealed record UpdateCategoryRequest(string Name);
public enum CommunitySidebarItemType { Channel, Category }
public enum CommunitySidebarDropIntent { Before, Inside, After, End, InsideAtStart }
public sealed record CommunitySidebarMoveRequest(
    Guid? TargetParentCategoryId,
    Guid? TargetItemId,
    CommunitySidebarItemType? TargetItemType,
    CommunitySidebarDropIntent Intent);
public sealed record CreateChannelRequest(string Name, Guid? CategoryId,
    CommunityChannelKind Kind = CommunityChannelKind.Text);
public sealed record UpdateChannelRequest(string Name, Guid? CategoryId,
    CommunityChannelKind Kind = CommunityChannelKind.Text);

public sealed record CommunityForumPostDto(
    Guid Id,
    Guid CommunityId,
    Guid ForumChannelId,
    Guid DiscussionChannelId,
    Guid RootMessageId,
    MessageAuthorDto Author,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastActivityAt,
    int ReplyCount,
    bool IsLocked,
    bool IsPinned,
    int UnreadCount = 0,
    string? RootPreview = null,
    IReadOnlyList<CommunityMentionDto>? RootMentions = null);

public sealed record CommunityForumPostPageDto(IReadOnlyList<CommunityForumPostDto> Posts, int? NextOffset);
public sealed record CreateCommunityForumPostRequest(string Title, SendChannelMessageRequest InitialMessage);
public sealed record UpdateCommunityForumPostRequest(string? Title = null, bool? IsLocked = null, bool? IsPinned = null);
public sealed record CommunityForumPostChangedEvent(Guid CommunityId, Guid ForumChannelId,
    CommunityForumPostDto? Post, Guid PostId, string Change, Guid? ActorAccountId = null);

public static class CommunityForumHubContract
{
    public const string PostChanged = "CommunityForumPostChanged";
}
