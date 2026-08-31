using System.Text;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

public enum CommunityChannelEmbedProvider
{
    GoogleDocs = 0,
    GoogleSheets = 1
}

public sealed record CommunityChannelEmbedUpdate(CommunityChannelEmbedProvider? Provider, string? Url);

public sealed record GoogleDocsEmbedConfiguration(string DocumentId, string OpenUrl, string FrameUrl,
    string? PublishedUrl = null, string? CanonicalUrl = null, string? AnonymousExportUrl = null,
    GoogleDocsInputKind InputKind = GoogleDocsInputKind.ShareLink)
{
    public string? FetchUrl => PublishedUrl ?? AnonymousExportUrl;
    public GoogleDocsFetchMode FetchMode => PublishedUrl is not null
        ? GoogleDocsFetchMode.PublishedHtml : GoogleDocsFetchMode.AnonymousExport;
}

public enum GoogleDocsInputKind { ShareLink, PublishedLink }
public enum GoogleDocsFetchMode { AnonymousExport, PublishedHtml }

public sealed record EmbeddedContentConfiguration(CommunityChannelEmbedProvider Provider, string SourceId,
    string OpenUrl, string FetchUrl, string? TabId = null)
{
    public string CacheIdentity => $"{Provider}:{SourceId}:{TabId ?? "default"}";
    public string RequestIdentity => TabId is null ? SourceId : $"{SourceId}~{TabId}";
    public string ProviderName => Provider == CommunityChannelEmbedProvider.GoogleSheets ? "Google Sheets" : "Google Docs";
    public string ContentName => Provider == CommunityChannelEmbedProvider.GoogleSheets ? "Spreadsheet" : "Document";
}

public sealed record GoogleSheetsEmbedConfiguration(string SpreadsheetId, string? Gid, string OpenUrl,
    string FetchUrl)
{
    public EmbeddedContentConfiguration ToContent() => new(CommunityChannelEmbedProvider.GoogleSheets,
        SpreadsheetId, OpenUrl, FetchUrl, Gid);
}

public enum ChannelEmbedDocumentStatus
{
    Ready,
    AuthenticationRequired,
    NotFound,
    Unsupported,
    ParseFailure,
    Timeout,
    TemporaryFailure,
    TooLarge
}
public sealed record ChannelEmbedDocumentDto(ChannelEmbedDocumentStatus Status, EmbeddedDocumentDto? Document,
    DateTimeOffset? FetchedAt = null, bool IsStale = false, string? ContentVersion = null,
    EmbeddedSheetDto? Sheet = null);

public sealed record EmbeddedSheetDto(string SpreadsheetId, string? Title,
    IReadOnlyList<EmbeddedSheetTabDto> Tabs, string? DefaultTabId);
public sealed record EmbeddedSheetTabDto(string Id, string Name, IReadOnlyList<EmbeddedSheetRowDto> Rows,
    IReadOnlyList<int> ColumnWidths, IReadOnlyList<EmbeddedSheetImageDto>? Images = null);
public sealed record EmbeddedSheetRowDto(int? Height, IReadOnlyList<EmbeddedSheetCellDto> Cells, int Index = 0);
public sealed record EmbeddedSheetCellDto(int Row, int Column, string DisplayValue, int RowSpan = 1,
    int ColumnSpan = 1, bool Bold = false, bool Italic = false, bool Underline = false,
    EmbeddedDocumentTextAlignment HorizontalAlignment = EmbeddedDocumentTextAlignment.Start,
    EmbeddedSheetVerticalAlignment VerticalAlignment = EmbeddedSheetVerticalAlignment.Middle,
    EmbeddedDocumentTextColor ForegroundColor = EmbeddedDocumentTextColor.Default,
    EmbeddedSheetCellColor BackgroundColor = EmbeddedSheetCellColor.Default,
    EmbeddedSheetBorderStyle TopBorder = EmbeddedSheetBorderStyle.None,
    EmbeddedSheetBorderStyle RightBorder = EmbeddedSheetBorderStyle.None,
    EmbeddedSheetBorderStyle BottomBorder = EmbeddedSheetBorderStyle.None,
    EmbeddedSheetBorderStyle LeftBorder = EmbeddedSheetBorderStyle.None,
    string? Link = null, bool IsCheckbox = false, bool? CheckboxValue = null,
    EmbeddedSheetFontSize FontSize = EmbeddedSheetFontSize.Normal,
    string? ForegroundHex = null, string? BackgroundHex = null,
    string? TopBorderColor = null, string? RightBorderColor = null,
    string? BottomBorderColor = null, string? LeftBorderColor = null,
    string? RawValue = null, string? FontFamily = null, double? FontSizePx = null,
    int? FontWeight = null, bool? WrapText = null);
public sealed record EmbeddedSheetImageDto(string MediaId, int AnchorRow, int AnchorColumn,
    int OffsetX, int OffsetY, int Width, int Height, string? Alt = null);
public enum EmbeddedSheetVerticalAlignment { Top, Middle, Bottom }
public enum EmbeddedSheetFontSize { Small, Normal, Medium, Large, Heading }
public enum EmbeddedSheetCellColor { Default, Light, Dark, Red, Orange, Yellow, Green, Teal, Blue, Purple, Pink, Gray }
public enum EmbeddedSheetBorderStyle { None, Thin, Medium, Thick, Dashed, Dotted }

public sealed record EmbeddedDocumentDto(IReadOnlyList<EmbeddedDocumentBlockDto> Blocks);

public enum EmbeddedDocumentTextAlignment { Start, Center, End, Justify }

public enum EmbeddedDocumentTextColor
{
    Default,
    Red,
    Orange,
    Yellow,
    Green,
    Teal,
    Blue,
    Purple,
    Pink,
    Gray
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EmbeddedDocumentParagraphDto), "paragraph")]
[JsonDerivedType(typeof(EmbeddedDocumentHeadingDto), "heading")]
[JsonDerivedType(typeof(EmbeddedDocumentImageDto), "image")]
[JsonDerivedType(typeof(EmbeddedDocumentListDto), "list")]
[JsonDerivedType(typeof(EmbeddedDocumentTableDto), "table")]
[JsonDerivedType(typeof(EmbeddedDocumentHorizontalRuleDto), "horizontalRule")]
[JsonDerivedType(typeof(EmbeddedDocumentSpacerDto), "spacer")]
public abstract record EmbeddedDocumentBlockDto;

public sealed record EmbeddedDocumentParagraphDto(IReadOnlyList<EmbeddedDocumentInlineDto> Content,
    EmbeddedDocumentTextAlignment Alignment = EmbeddedDocumentTextAlignment.Start) : EmbeddedDocumentBlockDto;
public sealed record EmbeddedDocumentHeadingDto(int Level, IReadOnlyList<EmbeddedDocumentInlineDto> Content,
    EmbeddedDocumentTextAlignment Alignment = EmbeddedDocumentTextAlignment.Start) : EmbeddedDocumentBlockDto;
public sealed record EmbeddedDocumentImageDto(string MediaId, string? Alt, int? Width = null, int? Height = null,
    EmbeddedDocumentTextAlignment Alignment = EmbeddedDocumentTextAlignment.Center) :
    EmbeddedDocumentBlockDto;
public sealed record EmbeddedDocumentListDto(bool Ordered, IReadOnlyList<EmbeddedDocumentListItemDto> Items) :
    EmbeddedDocumentBlockDto;
public sealed record EmbeddedDocumentListItemDto(IReadOnlyList<EmbeddedDocumentBlockDto> Blocks);
public sealed record EmbeddedDocumentTableDto(IReadOnlyList<EmbeddedDocumentTableRowDto> Rows) : EmbeddedDocumentBlockDto;
public sealed record EmbeddedDocumentTableRowDto(IReadOnlyList<EmbeddedDocumentTableCellDto> Cells);
public sealed record EmbeddedDocumentTableCellDto(bool IsHeader, int ColumnSpan, int RowSpan,
    IReadOnlyList<EmbeddedDocumentBlockDto> Blocks);
public sealed record EmbeddedDocumentHorizontalRuleDto : EmbeddedDocumentBlockDto;
public sealed record EmbeddedDocumentSpacerDto(int Lines = 1) : EmbeddedDocumentBlockDto;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EmbeddedDocumentTextDto), "text")]
[JsonDerivedType(typeof(EmbeddedDocumentLineBreakDto), "lineBreak")]
[JsonDerivedType(typeof(EmbeddedDocumentLinkDto), "link")]
public abstract record EmbeddedDocumentInlineDto;

public sealed record EmbeddedDocumentTextDto(string Text, bool Bold = false, bool Italic = false,
    bool Underline = false, EmbeddedDocumentTextColor TextColor = EmbeddedDocumentTextColor.Default) :
    EmbeddedDocumentInlineDto;
public sealed record EmbeddedDocumentLineBreakDto : EmbeddedDocumentInlineDto;
public sealed record EmbeddedDocumentLinkDto(string Url, IReadOnlyList<EmbeddedDocumentInlineDto> Content) :
    EmbeddedDocumentInlineDto;

public static class CommunityChannelEmbeds
{
    public const int MaximumMessageDocumentPreviews = 3;
    private static readonly Regex GoogleDocsUrl = new(
        @"https://docs\.google\.com/[^\s<>\]\)]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<GoogleDocsEmbedConfiguration> FindGoogleDocs(string? content,
        int maximum = MaximumMessageDocumentPreviews)
    {
        if (string.IsNullOrWhiteSpace(content) || maximum <= 0) return [];
        var limit = Math.Min(maximum, MaximumMessageDocumentPreviews);
        var found = new List<GoogleDocsEmbedConfiguration>(limit);
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in GoogleDocsUrl.Matches(content))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
            if (!TryGoogleDocs(candidate, out var configuration) || configuration is null ||
                !documentIds.Add(configuration.DocumentId)) continue;
            found.Add(configuration);
            if (found.Count == limit) break;
        }
        return found;
    }

    public static IReadOnlyList<EmbeddedContentConfiguration> FindSupportedContent(string? content,
        int maximum = MaximumMessageDocumentPreviews)
    {
        if (string.IsNullOrWhiteSpace(content) || maximum <= 0) return [];
        var limit = Math.Min(maximum, MaximumMessageDocumentPreviews);
        var found = new List<EmbeddedContentConfiguration>(limit);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in GoogleDocsUrl.Matches(content))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
            EmbeddedContentConfiguration? source = null;
            if (TryGoogleDocs(candidate, out var document) && document is not null)
                source = new(CommunityChannelEmbedProvider.GoogleDocs, document.DocumentId, document.OpenUrl,
                    document.FetchUrl!);
            else if (TryGoogleSheets(candidate, out var sheet) && sheet is not null) source = sheet.ToContent();
            if (source is null || !identities.Add(source.CacheIdentity)) continue;
            found.Add(source);
            if (found.Count == limit) break;
        }
        return found;
    }

    public static bool TryGoogleDocs(string? value, out GoogleDocsEmbedConfiguration? configuration)
    {
        configuration = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 5 && segments[0] == "document" && segments[1] == "d" &&
            segments[2] == "e" && IsSafeGoogleDocumentId(segments[3]) && segments[4] == "pub")
        {
            var publishedId = segments[3];
            var publishedUrl = $"https://docs.google.com/document/d/e/{publishedId}/pub";
            configuration = new(publishedId, publishedUrl, $"{publishedUrl}?embedded=true", publishedUrl, publishedUrl,
                InputKind: GoogleDocsInputKind.PublishedLink);
            return true;
        }
        if (segments.Length != 4 || segments[0] != "document" || segments[1] != "d" ||
            !IsSafeGoogleDocumentId(segments[2]) || segments[3] is not ("edit" or "view" or "preview" or "pub")) return false;
        var id = segments[2];
        var openUrl = $"https://docs.google.com/document/d/{id}/view";
        if (segments[3] == "pub")
        {
            var publishedUrl = $"https://docs.google.com/document/d/{id}/pub";
            configuration = new(id, openUrl, $"{publishedUrl}?embedded=true", publishedUrl, publishedUrl,
                InputKind: GoogleDocsInputKind.PublishedLink);
        }
        else configuration = new(id, openUrl, $"https://docs.google.com/document/d/{id}/preview",
            CanonicalUrl: openUrl,
            AnonymousExportUrl: $"https://docs.google.com/document/d/{id}/export?format=html");
        return true;
    }

    private static bool IsSafeGoogleDocumentId(string value) => value.Length is >= 10 and <= 200 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static bool TryGoogleSheets(string? value, out GoogleSheetsEmbedConfiguration? configuration)
    {
        configuration = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gid = QueryValue(uri.Query, "gid") ?? QueryValue(uri.Fragment, "gid");
        if (gid is not null && (gid.Length is < 1 or > 20 || !gid.All(char.IsAsciiDigit))) return false;
        if (segments.Length == 5 && segments[0] == "spreadsheets" && segments[1] == "d" &&
            segments[2] == "e" && IsSafeGoogleDocumentId(segments[3]) && segments[4] == "pubhtml")
        {
            var publishedId = segments[3];
            var published = $"https://docs.google.com/spreadsheets/d/e/{publishedId}/pubhtml";
            if (gid is not null) published += $"?gid={gid}";
            configuration = new(publishedId, gid, published, published);
            return true;
        }
        if (segments.Length != 4 || segments[0] != "spreadsheets" || segments[1] != "d" ||
            !IsSafeGoogleDocumentId(segments[2]) || segments[3] is not ("edit" or "view" or "preview")) return false;
        var id = segments[2];
        var suffix = gid is null ? string.Empty : $"?gid={gid}";
        // Anonymous XLSX export preserves workbook layout and final calculated values without exposing editor HTML.
        // The selected gid remains presentation state; one workbook import contains all visible tabs.
        var fetchUrl = $"https://docs.google.com/spreadsheets/d/{id}/export?format=xlsx";
        configuration = new(id, gid, $"https://docs.google.com/spreadsheets/d/{id}/view{suffix}", fetchUrl);
        return true;
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                try { return Uri.UnescapeDataString(parts[1]); }
                catch (UriFormatException) { return null; }
        }
        return null;
    }

    public static bool TryResolveContent(CommunityChannelEmbedProvider? provider, string? url,
        out EmbeddedContentConfiguration? configuration)
    {
        configuration = null;
        if (provider == CommunityChannelEmbedProvider.GoogleDocs && TryGoogleDocs(url, out var document) &&
            document?.FetchUrl is { } documentFetch)
            configuration = new(provider.Value, document.DocumentId, document.OpenUrl, documentFetch);
        else if (provider == CommunityChannelEmbedProvider.GoogleSheets && TryGoogleSheets(url, out var sheet) &&
                 sheet is not null) configuration = sheet.ToContent();
        return configuration is not null;
    }

    public static bool TryResolveContent(string? url, out EmbeddedContentConfiguration? configuration)
    {
        configuration = null;
        if (TryGoogleDocs(url, out var document) && document?.FetchUrl is { } documentFetch)
            configuration = new(CommunityChannelEmbedProvider.GoogleDocs, document.DocumentId,
                document.OpenUrl, documentFetch);
        else if (TryGoogleSheets(url, out var sheet) && sheet is not null)
            configuration = sheet.ToContent();
        return configuration is not null;
    }

    public static bool TryResolve(CommunityChannelEmbedProvider? provider, string? url,
        out GoogleDocsEmbedConfiguration? configuration)
    {
        configuration = null;
        return provider == CommunityChannelEmbedProvider.GoogleDocs && TryGoogleDocs(url, out configuration);
    }
}

public sealed record CommunityChannelDto(Guid Id, Guid CommunityId, Guid? CategoryId, string Name, int Position,
    DateTimeOffset CreatedAt, int UnreadCount = 0, int MentionCount = 0,
    CommunityChannelKind Kind = CommunityChannelKind.Text,
    CommunityPermission EffectivePermissions = CommunityPermission.None,
    bool PermissionsSyncedToCategory = false,
    bool IsPrivate = false,
    bool RequireTag = false,
    CommunityChannelEmbedProvider? EmbedProvider = null,
    string? EmbedUrl = null,
    bool AllowDocumentEmbeds = false);
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
    CommunityChannelKind Kind = CommunityChannelKind.Text, bool? RequireTag = null,
    CommunityChannelEmbedUpdate? Embed = null, bool? AllowDocumentEmbeds = null);

public static class CommunityForumTagLimits
{
    public const int MaximumNameLength = 20;
    public const int MaximumTagsPerPost = 5;
    public const int MaximumTagsPerForum = 20;
}

public sealed record CommunityForumTagDto(Guid Id, Guid ChannelId, string Name,
    ReactionEmojiKind? EmojiKind = null, string? StandardEmoji = null, Guid? CustomEmojiId = null,
    bool CustomEmojiAvailable = true, bool Moderated = false, int SortOrder = 0,
    DateTimeOffset CreatedAt = default);
public sealed record CreateCommunityForumTagRequest(string Name, ReactionEmojiKind? EmojiKind = null,
    string? StandardEmoji = null, Guid? CustomEmojiId = null, bool Moderated = false);
public sealed record UpdateCommunityForumTagRequest(string Name, ReactionEmojiKind? EmojiKind = null,
    string? StandardEmoji = null, Guid? CustomEmojiId = null, bool Moderated = false,
    int? SortOrder = null);
public sealed record ReorderCommunityForumTagsRequest(IReadOnlyList<Guid> TagIds);
public sealed record UpdateCommunityForumPostTagsRequest(IReadOnlyList<Guid> TagIds);
public sealed record CommunityForumTagsChangedEvent(Guid CommunityId, Guid ForumChannelId,
    IReadOnlyList<CommunityForumTagDto> Tags);

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
    IReadOnlyList<CommunityMentionDto>? RootMentions = null,
    IReadOnlyList<CommunityForumTagDto>? Tags = null,
    CommunityChannelEmbedProvider? EmbedProvider = null,
    string? EmbedUrl = null);

public sealed record CommunityForumPostPageDto(IReadOnlyList<CommunityForumPostDto> Posts, int? NextOffset);
public sealed record CreateCommunityForumPostRequest(string Title, SendChannelMessageRequest InitialMessage,
    IReadOnlyList<Guid>? TagIds = null, CommunityChannelEmbedUpdate? Embed = null);
public sealed record UpdateCommunityForumPostRequest(string? Title = null, bool? IsLocked = null,
    bool? IsPinned = null, CommunityChannelEmbedUpdate? Embed = null);
public sealed record CommunityForumPostChangedEvent(Guid CommunityId, Guid ForumChannelId,
    CommunityForumPostDto? Post, Guid PostId, string Change, Guid? ActorAccountId = null);

public static class CommunityForumHubContract
{
    public const string PostChanged = "CommunityForumPostChanged";
    public const string TagsChanged = "CommunityForumTagsChanged";
}
