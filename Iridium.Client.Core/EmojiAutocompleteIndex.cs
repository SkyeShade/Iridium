using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record EmojiSearchResult(string Alias, StandardEmoji? Standard, AvailableCommunityEmoji? Custom);
public readonly record struct EmojiAliasRange(int Start, int End, string Alias);

public sealed class EmojiAutocompleteIndex
{
    private static readonly StandardIndex Standard = new();
    private IReadOnlyList<AvailableCommunityEmoji> _custom = [];
    private IReadOnlyDictionary<string, AvailableCommunityEmoji[]> _customExact =
        new Dictionary<string, AvailableCommunityEmoji[]>(StringComparer.OrdinalIgnoreCase);
    private string _customRevision = string.Empty;
    private IReadOnlyList<AvailableCommunityEmoji>? _customSource;

    public int CustomBuildCount { get; private set; }
    public static int StandardBuildCount => Standard.BuildCount;

    public void UpdateCustom(IReadOnlyList<AvailableCommunityEmoji> values)
    {
        if (ReferenceEquals(values, _customSource)) return;
        _customSource = values;
        var ordered = CommunityEmojiDraftCodec.Order(values, null);
        var revision = string.Join('|', ordered.Select(value => $"{value.Community.Id:N}:{value.Emoji.Id:N}:{value.Emoji.Revision}"));
        if (revision == _customRevision) return;
        _customRevision = revision;
        _custom = ordered;
        _customExact = ordered.GroupBy(value => value.Emoji.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.OrdinalIgnoreCase);
        CustomBuildCount++;
    }

    public EmojiSearchResult? Exact(string alias, bool allowCustom)
    {
        var normalized = alias.Trim().ToLowerInvariant();
        if (Standard.Exact.TryGetValue(normalized, out var standard)) return new(standard.Name, standard, null);
        return allowCustom && _customExact.TryGetValue(normalized, out var custom) && custom.Length > 0
            ? new(custom[0].Emoji.Name, null, custom[0])
            : null;
    }

    public AvailableCommunityEmoji? ExactCustom(string alias) =>
        _customExact.TryGetValue(alias, out var values) && values.Length > 0 ? values[0] : null;

    public IReadOnlyList<EmojiSearchResult> Search(string query, int limit = 8, bool allowCustom = true)
    {
        var normalized = query.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || limit <= 0) return [];
        return Standard.Entries.Select(value => (Rank: Rank(value.Aliases, normalized), Result: new EmojiSearchResult(value.Emoji.Name, value.Emoji, null)))
            .Concat(allowCustom ? _custom.Select(value => (Rank: Rank([value.Emoji.Name], normalized), Result: new EmojiSearchResult(value.Emoji.Name, null, value))) : [])
            .Where(value => value.Rank < 3)
            .OrderBy(value => value.Rank).ThenBy(value => value.Result.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Result.Custom?.Community.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(value => value.Result).ToArray();
    }

    public static bool TryAliasBeforeCaret(string content, int caret, out EmojiAliasRange range)
    {
        range = default;
        if (caret < 4 || caret > content.Length || content[caret - 1] != ':') return false;
        var start = content.LastIndexOf(':', caret - 2);
        if (start < 0 || start > 0 && char.IsLetterOrDigit(content[start - 1])) return false;
        var alias = content[(start + 1)..(caret - 1)];
        if (!CommunityEmojiNames.IsValid(alias)) return false;
        range = new(start, caret, alias);
        return true;
    }

    private static int Rank(IEnumerable<string> aliases, string query)
    {
        var rank = 3;
        foreach (var alias in aliases)
        {
            if (alias.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (alias.StartsWith(query, StringComparison.OrdinalIgnoreCase)) rank = Math.Min(rank, 1);
            else if (alias.Contains(query, StringComparison.OrdinalIgnoreCase)) rank = Math.Min(rank, 2);
        }
        return rank;
    }

    private sealed class StandardIndex
    {
        public int BuildCount { get; } = 1;
        public IReadOnlyList<(StandardEmoji Emoji, string[] Aliases)> Entries { get; } = StandardEmojiCatalog.All
            .Select(value => (value, new[] { value.Name }.Concat(value.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        public IReadOnlyDictionary<string, StandardEmoji> Exact { get; } = StandardEmojiCatalog.All
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => value.First(), StringComparer.OrdinalIgnoreCase);
    }
}
