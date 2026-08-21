using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.Components.Web;

namespace Iridium.Web.Models;

public sealed record AuthenticationSubmission(
    bool IsRegister,
    string Username,
    string DisplayName,
    string Password,
    string ConfirmPassword);

public sealed record ProfileSubmission(string DisplayName, string? Pronouns, string? Description);

public sealed record CommunitySubmission(string Name, string? Description);

public sealed record ChannelSettingsSubmission(Guid? ChannelId, string Name, Guid? CategoryId);

public sealed record CategorySettingsSubmission(Guid? CategoryId, string Name, Guid? ParentCategoryId = null);

public sealed record CommunitySidebarVisualItem(
    CommunitySidebarItemType Type,
    Guid Id,
    Guid? ParentCategoryId,
    int Position,
    CommunityCategoryDto? Category,
    CommunityChannelDto? Channel)
{
    public static CommunitySidebarVisualItem From(CommunityCategoryDto value) =>
        new(CommunitySidebarItemType.Category, value.Id, value.ParentCategoryId, value.Position, value, null);
    public static CommunitySidebarVisualItem From(CommunityChannelDto value) =>
        new(CommunitySidebarItemType.Channel, value.Id, value.CategoryId, value.Position, null, value);
}

public sealed record MessageEditSubmission(Guid MessageId, string Content);
public sealed record MessageSubmission(string Content, IReadOnlyList<CommunityMentionInput> Mentions,
    IReadOnlyList<AttachmentDto> Attachments,
    Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>? UploadAttachments = null);
public sealed record MentionProfileClick(Guid AccountId, MouseEventArgs Pointer);
public sealed record ProfileCardPosition(double X, double Y);
public sealed record CommunityMemberRolesChange(Guid AccountId, IReadOnlyList<Guid> RoleIds);
