using Iridium.Protocol;

namespace Iridium.Client.Core;

public static class CommunityForumPostSearch
{
    public static IReadOnlyList<CommunityForumPostDto> Filter(
        IEnumerable<CommunityForumPostDto> posts,
        string? query,
        IReadOnlyCollection<Guid>? tagIds = null)
    {
        var ordered = posts.ToArray();
        var term = query?.Trim();
        var selected = tagIds?.ToHashSet() ?? [];

        return ordered.Where(post => (selected.Count == 0 || (post.Tags ?? []).Any(tag => selected.Contains(tag.Id))) &&
                (string.IsNullOrEmpty(term) || Contains(post.Title, term) ||
                Contains(post.RootPreview, term) || Contains(post.Author.DisplayName, term) ||
                Contains(post.Author.Username, term)))
            .ToArray();
    }

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
