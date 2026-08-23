namespace Iridium.Web.Models;

public static class StandardEmojiArtworkSource
{
    private static readonly HashSet<string> LocalArtwork =
    [
        "1f1e9-1f1f0", "1f308", "1f389", "1f431", "1f436", "1f44b", "1f44d-1f3fd", "1f44d",
        "1f44e", "1f44f", "1f469-200d-1f4bb", "1f525", "1f600", "1f602", "1f60d", "1f60e",
        "1f62d", "1f64f", "1f914", "2705", "2728", "274c", "2764"
    ];

    public static string Resolve(string artworkKey) => LocalArtwork.Contains(artworkKey)
        ? $"/vendor/twemoji/svg/{artworkKey}.svg"
        : $"https://cdn.jsdelivr.net/gh/jdecked/twemoji@v17.0.3/assets/svg/{artworkKey}.svg";
}
