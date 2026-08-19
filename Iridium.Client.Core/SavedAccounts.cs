using Iridium.Protocol;

namespace Iridium.Client.Core;

public enum SavedAccountStatus
{
    Ready,
    LoginRequired
}

public readonly record struct SavedAccountKey(string NodeAddress, Guid AccountId);

public sealed record SavedAccount(
    string NodeAddress,
    Guid AccountId,
    string Username,
    string DisplayName,
    string? Pronouns,
    UserPresence PreferredPresence,
    SavedAccountStatus Status,
    string? NodeIdentityAuthority = null)
{
    public SavedAccountKey Key => new(NodeAddress, AccountId);
    public string PublicIdentity => IridiumIdentity.Format(
        Username,
        string.IsNullOrWhiteSpace(NodeIdentityAuthority)
            ? IridiumIdentity.AuthorityFromEndpoint(NodeAddress)
            : NodeIdentityAuthority);
}

// Storage-only model. SessionToken is deliberately not exposed through NodeSession.SavedAccounts.
public sealed record SavedAccountRecord(
    string NodeAddress,
    Guid AccountId,
    string Username,
    string DisplayName,
    string? Pronouns,
    UserPresence PreferredPresence,
    string? SessionToken,
    SavedAccountStatus Status,
    string? NodeIdentityAuthority = null);

public sealed record SavedAccountStoreData(
    SavedAccountRecord[] Accounts,
    string? ActiveNodeAddress,
    Guid? ActiveAccountId)
{
    public static SavedAccountStoreData Empty { get; } = new([], null, null);
}

public interface ISavedAccountStore
{
    Task<SavedAccountStoreData> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SavedAccountStoreData data, CancellationToken cancellationToken = default);
}

public interface IActiveAccountSelectionStore
{
    Task<SavedAccountKey?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SavedAccountKey? key, CancellationToken cancellationToken = default);
}
