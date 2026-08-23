namespace Iridium.Client.Core;

using System.Diagnostics;
using Iridium.Protocol;

public sealed record SavedNode(string Address, string? Label, bool IsLocal = false, string? IdentityAuthority = null)
{
    public string PublicAuthority => string.IsNullOrWhiteSpace(IdentityAuthority)
        ? IridiumIdentity.AuthorityFromEndpoint(Address)
        : IdentityAuthority.Trim().ToLowerInvariant();
}
public sealed record NodeAvailability(SavedNode Node, ServerInfoDto? Info, long? LatencyMs, string? Error)
{
    public bool IsOnline => Info is not null;
}

public interface ISavedNodeStore
{
    Task<IReadOnlyList<SavedNode>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<SavedNode> nodes, CancellationToken cancellationToken = default);
}

public interface INodeTokenStore
{
    Task<string?> LoadAsync(string nodeAddress, CancellationToken cancellationToken = default);
    Task SaveAsync(string nodeAddress, string token, CancellationToken cancellationToken = default);
    Task RemoveAsync(string nodeAddress, CancellationToken cancellationToken = default);
}

public sealed class SavedNodeState(ISavedNodeStore store)
{
    private readonly List<SavedNode> _nodes = [];

    public IReadOnlyList<SavedNode> Nodes => _nodes;

    public async Task<SavedNode> InitializeAsync(
        SavedNode defaultNode,
        bool persistDefaultNode = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedDefault = defaultNode with { Address = NormalizeAddress(defaultNode.Address) };
        var savedNodes = new List<SavedNode>();
        foreach (var saved in await store.LoadAsync(cancellationToken))
        {
            var normalizedSaved = saved with { Address = NormalizeAddress(saved.Address), IsLocal = false };
            if (savedNodes.All(node => !SameAddress(node.Address, normalizedSaved.Address)))
                savedNodes.Add(normalizedSaved);
        }

        var matchingSaved = savedNodes.FirstOrDefault(node => SameAddress(node.Address, normalizedDefault.Address));
        var selectedDefault = persistDefaultNode && matchingSaved is not null ? matchingSaved : normalizedDefault;

        _nodes.Clear();
        _nodes.Add(selectedDefault);
        foreach (var saved in savedNodes)
        {
            if (!SameAddress(selectedDefault.Address, saved.Address)) _nodes.Add(saved);
        }

        if (persistDefaultNode) await PersistAsync(cancellationToken);
        return selectedDefault;
    }

    public async Task<SavedNode> AddAsync(string address, string? label = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAddress(address);
        var existing = _nodes.FirstOrDefault(node => SameAddress(node.Address, normalized));
        if (existing is not null) return existing;

        var node = new SavedNode(normalized, string.IsNullOrWhiteSpace(label) ? null : label.Trim());
        _nodes.Add(node);
        await PersistAsync(cancellationToken);
        return node;
    }

    public async Task RemoveAsync(SavedNode node, CancellationToken cancellationToken = default)
    {
        if (node.IsLocal) return;
        _nodes.Remove(node);
        await PersistAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NodeAvailability>> ProbeAllAsync(CancellationToken cancellationToken = default) =>
        await Task.WhenAll(_nodes.Select(node => ProbeAsync(node, cancellationToken)));

    private static async Task<NodeAvailability> ProbeAsync(SavedNode node, CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var info = await new NodeClient(new Uri(node.Address)).GetServerInfoAsync(cancellationToken);
            return new NodeAvailability(node, info, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception exception)
        {
            return new NodeAvailability(node, null, null, exception.GetBaseException().Message);
        }
    }

    private Task PersistAsync(CancellationToken cancellationToken) =>
        store.SaveAsync(_nodes.Where(node => !node.IsLocal).ToArray(), cancellationToken);

    private static bool SameAddress(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static string NormalizeAddress(string value)
    {
        var address = value.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid HTTP or HTTPS Node address.", nameof(value));
        return uri.ToString().TrimEnd('/');
    }
}
