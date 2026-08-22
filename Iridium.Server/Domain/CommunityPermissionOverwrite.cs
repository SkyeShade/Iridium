using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class CommunityPermissionOverwrite
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public PermissionOverwriteScopeType ScopeType { get; set; }
    public Guid ScopeId { get; set; }
    public PermissionOverwriteTargetType TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public CommunityPermission Allow { get; set; }
    public CommunityPermission Deny { get; set; }
    public required Community Community { get; set; }
}
