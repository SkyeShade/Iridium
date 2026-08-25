using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class CommunityForumPostSearch
{
    public static IReadOnlyList<CommunityForumPostDto> Filter(
        IEnumerable<CommunityForumPostDto> posts,
        string? query)
    {
        var ordered = posts.ToArray();
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term)) return ordered;

        return ordered.Where(post =>
                Contains(post.Title, term) ||
                Contains(post.RootPreview, term) ||
                Contains(post.Author.DisplayName, term) ||
                Contains(post.Author.Username, term))
            .ToArray();
    }

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
