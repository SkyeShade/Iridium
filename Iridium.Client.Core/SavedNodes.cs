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

    public async Task InitializeAsync(SavedNode localNode, CancellationToken cancellationToken = default)
    {
        _nodes.Clear();
        _nodes.Add(localNode);
        foreach (var saved in await store.LoadAsync(cancellationToken))
        {
            if (_nodes.All(node => !SameAddress(node.Address, saved.Address)))
                _nodes.Add(saved with { IsLocal = false });
        }
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
