using System.Globalization;
using System.Text.RegularExpressions;

namespace Iridium.Client.Core;

public enum ExternalEmbedKind { YouTube }

public sealed record ExternalEmbedReference(
    ExternalEmbedKind Kind,
    string Provider,
    string VideoId,
    string OriginalUrl,
    string ThumbnailUrl,
    string EmbedUrl,
    int? StartSeconds = null);

public interface IExternalEmbedProvider
{
    ExternalEmbedReference? Resolve(Uri uri);
}

public sealed partial class YouTubeEmbedProvider : IExternalEmbedProvider
{
    private const int MaximumStartSeconds = 7 * 24 * 60 * 60;

    public ExternalEmbedReference? Resolve(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        string? id = null;
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com")
        {
            if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
                id = Query(uri, "v");
            else
            {
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)) id = parts[1];
            }
        }
        else if (host == "youtu.be")
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) id = parts[0];
        }

        if (id is null || !VideoId().IsMatch(id)) return null;
        var start = ParseStart(Query(uri, "t") ?? Query(uri, "start"));
        var embed = $"https://www.youtube-nocookie.com/embed/{id}" +
                    (start is { } seconds ? $"?start={seconds.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
        return new(ExternalEmbedKind.YouTube, "YouTube", id, uri.AbsoluteUri,
            $"https://i.ytimg.com/vi/{id}/hqdefault.jpg", embed, start);
    }

    private static string? Query(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(separator < 0 ? string.Empty : part[(separator + 1)..]);
        }
        return null;
    }

    internal static int? ParseStart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().ToLowerInvariant();
        if (int.TryParse(value.TrimEnd('s'), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return seconds is > 0 and <= MaximumStartSeconds ? seconds : null;
        var match = Timestamp().Match(value);
        if (!match.Success) return null;
        var total = Value(match, "h") * 3600 + Value(match, "m") * 60 + Value(match, "s");
        return total is > 0 and <= MaximumStartSeconds ? total : null;
    }

    private static int Value(Match match, string group) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoId();

    [GeneratedRegex("^(?:(?<h>\\d+)h)?(?:(?<m>\\d+)m)?(?:(?<s>\\d+)s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
}

public sealed class ExternalEmbedResolver(IEnumerable<IExternalEmbedProvider> providers)
{
    public const int MaximumEmbedsPerMessage = 3;
    private readonly IExternalEmbedProvider[] _providers = providers.ToArray();

    public IReadOnlyList<ExternalEmbedReference> Resolve(string content, int maximum = MaximumEmbedsPerMessage)
    {
        if (string.IsNullOrWhiteSpace(content) || maximum <= 0) return [];
        var result = new List<ExternalEmbedReference>(Math.Min(maximum, MaximumEmbedsPerMessage));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in Links(MessageContentSegments.Parse(content, null)))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var embed = _providers.Select(provider => provider.Resolve(uri)).FirstOrDefault(value => value is not null);
            if (embed is null || !seen.Add(embed.EmbedUrl)) continue;
            result.Add(embed);
            if (result.Count >= Math.Min(maximum, MaximumEmbedsPerMessage)) break;
        }
        return result;
    }

    private static IEnumerable<string> Links(IEnumerable<MessageContentNode> nodes)
    {
        foreach (var node in nodes)
            switch (node)
            {
                case MessageLinkNode link:
                    yield return link.Url;
                    break;
                case MessageContainerNode container when container.Kind != MessageContentKind.Spoiler:
                    foreach (var value in Links(container.Children)) yield return value;
                    break;
                case MessageListNode list:
                    foreach (var item in list.Items)
                    foreach (var value in Links(item.Children)) yield return value;
                    break;
            }
    }
}
