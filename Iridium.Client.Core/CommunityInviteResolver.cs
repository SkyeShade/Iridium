using Iridium.Protocol;

namespace Iridium.Client.Core;

public interface ICommunityInviteResolver
{
    CommunityInviteReference? Find(string content);
    Task<CommunityInvitePreviewDto> ResolveAsync(CommunityInviteReference invite, CancellationToken cancellationToken = default);
    Task<JoinCommunityInviteResultDto> JoinAsync(CommunityInviteReference invite, CancellationToken cancellationToken = default);
}

public sealed class CommunityInviteResolver(NodeSession session) : ICommunityInviteResolver
{
    public CommunityInviteReference? Find(string content) => CommunityInviteLink.Find(content);

    public async Task<CommunityInvitePreviewDto> ResolveAsync(
        CommunityInviteReference invite, CancellationToken cancellationToken = default)
    {
        if (!IsCurrentNode(invite.NodeAuthority))
            return new CommunityInvitePreviewDto(CommunityInviteStatus.AuthenticationRequiredOnTargetNode,
                null, null, null, 0, invite.NodeAuthority, false, null);
        return await session.ResolveCommunityInviteAsync(invite.Token, cancellationToken);
    }

    public Task<JoinCommunityInviteResultDto> JoinAsync(
        CommunityInviteReference invite, CancellationToken cancellationToken = default)
    {
        if (!IsCurrentNode(invite.NodeAuthority))
            throw new InvalidOperationException("Sign in with an account on the invite's Node before joining.");
        return session.JoinCommunityInviteAsync(invite.Token, cancellationToken);
    }

    private bool IsCurrentNode(string authority)
    {
        if (!session.IsAuthenticated || session.SelectedNode is null) return false;
        if (string.IsNullOrWhiteSpace(authority)) return true;
        if (string.Equals(authority, session.SelectedNode.PublicAuthority, StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(session.SelectedNode.Address, UriKind.Absolute, out var node) &&
               string.Equals(authority, node.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
