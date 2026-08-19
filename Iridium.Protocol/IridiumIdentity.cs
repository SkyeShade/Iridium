namespace Iridium.Protocol;

public readonly record struct IridiumIdentity(string Username, string NodeAuthority)
{
    public override string ToString() => Format(Username, NodeAuthority);

    public static string Format(string username, string nodeAuthority) =>
        $"{username.Trim().ToLowerInvariant()}@{NormalizeAuthority(nodeAuthority)}";

    public static bool TryParse(string? value, out IridiumIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf('@');
        if (separator <= 0 || separator == trimmed.Length - 1 || trimmed.IndexOf('@') != separator) return false;

        var username = trimmed[..separator].Trim().ToLowerInvariant();
        var authority = NormalizeAuthority(trimmed[(separator + 1)..]);
        if (username.Length is < 1 or > 32 || authority.Length == 0) return false;
        if (username.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))) return false;
        if (!Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var uri) || uri.Authority != authority) return false;

        identity = new IridiumIdentity(username, authority);
        return true;
    }

    public static string AuthorityFromEndpoint(string endpoint) => new Uri(endpoint).Authority.ToLowerInvariant();

    private static string NormalizeAuthority(string value) => value.Trim().TrimEnd('/').ToLowerInvariant();
}
