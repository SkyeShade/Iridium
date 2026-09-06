namespace Iridium.Client.Core;

public readonly record struct CommunityMemberListPreferenceScope(string NodeAuthority, Guid AccountId);

public interface ICommunityMemberListPreferenceStore
{
    Task<bool?> LoadAsync(CommunityMemberListPreferenceScope scope,
        CancellationToken cancellationToken = default);
    Task SaveAsync(CommunityMemberListPreferenceScope scope, bool visible,
        CancellationToken cancellationToken = default);
}
