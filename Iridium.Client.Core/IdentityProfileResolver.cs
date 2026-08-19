using System.Net;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public enum ProfileResolutionState
{
    Invalid,
    RemoteUnsupported,
    NotFound,
    Resolved,
    Failed
}

public sealed record ProfileResolutionResult(
    ProfileResolutionState State,
    IridiumIdentity? Identity = null,
    ResolvedProfileDto? Profile = null,
    string? Message = null);

public interface IIdentityProfileResolver
{
    Task<ProfileResolutionResult> ResolveAsync(string identity, CancellationToken cancellationToken = default);
}

public sealed class SameNodeIdentityProfileResolver(NodeSession session) : IIdentityProfileResolver
{
    public async Task<ProfileResolutionResult> ResolveAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (!IridiumIdentity.TryParse(identity, out var parsed))
            return new(ProfileResolutionState.Invalid, Message: "Enter a complete identity such as skye@friends.example.");
        var currentAuthority = session.SelectedNode?.PublicAuthority;
        if (!string.Equals(parsed.NodeAuthority, currentAuthority, StringComparison.OrdinalIgnoreCase))
            return new(ProfileResolutionState.RemoteUnsupported, parsed,
                Message: "Cross-Node profile resolution is not available yet.");
        try
        {
            return new(ProfileResolutionState.Resolved, parsed,
                await session.ResolveProfileAsync(parsed.Username, cancellationToken));
        }
        catch (NodeApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new(ProfileResolutionState.NotFound, parsed, Message: exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(ProfileResolutionState.Failed, parsed, Message: exception.Message);
        }
    }
}
