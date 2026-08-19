using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed class AccountSwitchService(
    NodeSession session,
    CommunitySession communities,
    ChannelMessagingSession messaging)
{
    public async Task InitializeAsync(IReadOnlyList<SavedNode> nodes, CancellationToken cancellationToken = default)
    {
        await session.InitializeAsync(nodes, cancellationToken);
        if (session.IsAuthenticated) await messaging.ConnectAsync(cancellationToken);
    }

    public void BeginAuthentication(SavedNode node, SavedAccountKey? reauthenticationKey = null) =>
        session.BeginAuthentication(node, reauthenticationKey);

    public async Task<bool> SwitchAsync(SavedAccountKey key, CancellationToken cancellationToken = default)
    {
        var activation = await session.PrepareSwitchAsync(key, cancellationToken);
        if (activation is null) return false;
        await ResetAccountContextAsync(cancellationToken);
        await session.ActivateSwitchAsync(activation, cancellationToken);
        await messaging.ConnectAsync(cancellationToken);
        return true;
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var result = await session.AuthenticateLoginAsync(username, password, cancellationToken);
        await ActivateAuthenticationAsync(result, cancellationToken);
    }

    public async Task RegisterAsync(
        string username,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var result = await session.AuthenticateRegistrationAsync(username, displayName, password, cancellationToken);
        await ActivateAuthenticationAsync(result, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await ResetAccountContextAsync(cancellationToken);
        await session.LogoutActiveAsync(cancellationToken);
    }

    public async Task RemoveFromDeviceAsync(SavedAccountKey key, CancellationToken cancellationToken = default)
    {
        if (session.ActiveSavedAccount?.Key is { } active && active == key)
            await ResetAccountContextAsync(cancellationToken);
        await session.RemoveAccountAsync(key, cancellationToken);
    }

    private async Task ActivateAuthenticationAsync(
        AuthenticationResultDto result,
        CancellationToken cancellationToken)
    {
        var activation = await session.PrepareAuthenticationAsync(result, cancellationToken);
        await ResetAccountContextAsync(cancellationToken);
        await session.AcceptAuthenticationAsync(activation, cancellationToken);
        await messaging.ConnectAsync(cancellationToken);
    }

    private async Task ResetAccountContextAsync(CancellationToken cancellationToken)
    {
        await messaging.DisconnectAsync(cancellationToken);
        communities.Clear();
    }
}
