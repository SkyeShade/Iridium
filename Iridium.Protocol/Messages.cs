namespace Iridium.Protocol;

public sealed record ChatMessageDto(
    MessageId Id,
    ConversationId ConversationId,
    UserId SenderId,
    string Content,
    DateTimeOffset SentAt);

public sealed record SendChatMessage(ConversationId ConversationId, string Content);
public sealed record ChatMessageReceived(ChatMessageDto Message);
public sealed record ServerInfoDto(
    string Name,
    string Motd,
    int ProtocolVersion,
    int OnlineUsers,
    int? MaximumUsers,
    string? ServerIconUrl);

public sealed record RegisterAccountRequest(string Username, string DisplayName, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record NodeAccountDto(
    Guid Id,
    string Username,
    string DisplayName,
    string? Pronouns,
    string? Description,
    UserPresence PreferredPresence,
    DateTimeOffset CreatedAt);
public sealed record AuthenticationResultDto(string AccessToken, NodeAccountDto Account);
public sealed record UpdateProfileRequest(string DisplayName, string? Pronouns, string? Description);
public sealed record CommunityDto(Guid Id, string Name, string? Description, Guid OwnerAccountId, DateTimeOffset CreatedAt);
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
    PublicPresence Presence)
{
    public bool IsOnline => Presence != PublicPresence.Offline;
}

public sealed record SendFriendRequest(string Username);

public sealed record CommunityCategoryDto(Guid Id, Guid CommunityId, string Name, int Position, Guid? ParentCategoryId);
public sealed record CommunityChannelDto(Guid Id, Guid CommunityId, Guid? CategoryId, string Name, int Position, DateTimeOffset CreatedAt);
public sealed record CommunityStructureDto(
    Guid CommunityId,
    bool CanManage,
    IReadOnlyList<CommunityCategoryDto> Categories,
    IReadOnlyList<CommunityChannelDto> Channels,
    CommunityPermission EffectivePermissions = CommunityPermission.None,
    bool IsOwner = false);
public sealed record CreateCategoryRequest(string Name);
public sealed record UpdateCategoryRequest(string Name);
public sealed record MoveCategoryRequest(int Position);
public sealed record CreateChannelRequest(string Name, Guid? CategoryId);
public sealed record UpdateChannelRequest(string Name, Guid? CategoryId);
public sealed record MoveChannelRequest(Guid? CategoryId, int Position);
