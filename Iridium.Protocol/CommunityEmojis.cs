using System.Text.RegularExpressions;

namespace Iridium.Protocol;

public static class CommunityEmojiLimits
{
    public const int MaximumPerCommunity = 100;
    public const long MaximumUploadBytes = 500_000;
    public const long MaximumMultipartBytes = 650_000;
    public const int MaximumNameLength = 32;
    public const int ProcessedDimension = 128;
    public const int MaximumLargeMessageEmoji = 5;
}

public sealed record CommunityEmojiDto(Guid Id, Guid CommunityId, string Name, string ContentType,
    bool IsAnimated, int Width, int Height, long SizeBytes, long Revision, DateTimeOffset CreatedAt,
    Guid CreatedByAccountId);
public sealed record RenameCommunityEmojiRequest(string Name);
public sealed record EmojiSelection(string InsertText, string Name, Guid? CustomEmojiId = null,
    Guid? CommunityId = null, string? StandardArtworkKey = null);
public sealed record CommunityEmojiReference(Guid EmojiId, string Name);

public static partial class CommunityEmojiNames
{
    [GeneratedRegex("^[a-z0-9_]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPattern();
    [GeneratedRegex("<:([a-z0-9_]{2,32}):([0-9a-fA-F]{32})>", RegexOptions.CultureInvariant)]
    private static partial Regex InternalTokenPattern();

    public static string Normalize(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value).Trim().ToLowerInvariant();
        name = Regex.Replace(name, "[^a-z0-9]+", "_", RegexOptions.CultureInvariant).Trim('_');
        return name.Length > CommunityEmojiLimits.MaximumNameLength ? name[..CommunityEmojiLimits.MaximumNameLength] : name;
    }

    public static bool IsValid(string value) => ValidPattern().IsMatch(value);
    public static string Token(Guid id, string name) => $"<:{name}:{id:N}>";
    public static string ToUserFacing(string content) => InternalTokenPattern().Replace(content, ":$1:");
    public static string ToCharacterCountingText(string content) => InternalTokenPattern().Replace(content, "x");
    public static IReadOnlyList<CommunityEmojiReference> References(string content) => InternalTokenPattern()
        .Matches(content).Select(match => new CommunityEmojiReference(
            Guid.ParseExact(match.Groups[2].Value, "N"), match.Groups[1].Value)).ToArray();
}

public sealed record StandardEmoji(string Glyph, string Name, string Category, string ArtworkKey,
    params string[] Keywords);

public static class StandardEmojiCatalog
{
    private static readonly IReadOnlyDictionary<string, string> PrimaryAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["1f44b"] = "wave"
    };
    public static IReadOnlyList<StandardEmoji> All { get; } = Load();
    private static readonly IReadOnlyDictionary<char, StandardEmoji[]> ByFirstCharacter = All
        .GroupBy(value => value.Glyph[0]).ToDictionary(value => value.Key,
            value => value.OrderByDescending(emoji => emoji.Glyph.Length).ToArray());

    public static StandardEmoji? MatchAt(string text, int position)
    {
        if (position < 0 || position >= text.Length || !ByFirstCharacter.TryGetValue(text[position], out var candidates))
            return null;
        return candidates.FirstOrDefault(value => text.AsSpan(position).StartsWith(value.Glyph,
            StringComparison.Ordinal));
    }

    private static IReadOnlyList<StandardEmoji> Load()
    {
        using var stream = typeof(StandardEmojiCatalog).Assembly
            .GetManifestResourceStream("Iridium.Protocol.emoji-test.txt")
            ?? throw new InvalidOperationException("The bundled Unicode emoji metadata is missing.");
        using var reader = new StreamReader(stream);
        var values = new List<StandardEmoji>(4000);
        var group = "Symbols";
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# group: ", StringComparison.Ordinal))
            {
                group = line[9..].Trim();
                continue;
            }
            if (group == "Component" || !line.Contains("; fully-qualified", StringComparison.Ordinal)) continue;
            var semicolon = line.IndexOf(';');
            var hash = line.IndexOf('#');
            if (semicolon <= 0 || hash <= semicolon) continue;
            var codePoints = line[..semicolon].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Convert.ToInt32(value, 16)).ToArray();
            var glyph = string.Concat(codePoints.Select(char.ConvertFromUtf32));
            var description = line[(hash + 1)..].Trim();
            var nameStart = description.IndexOf(' ');
            if (nameStart < 0) continue;
            nameStart = description.IndexOf(' ', nameStart + 1);
            if (nameStart < 0) continue;
            var displayName = description[(nameStart + 1)..].Trim();
            var name = NormalizeName(displayName);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var artwork = string.Join('-', codePoints.Where(value => value != 0xFE0F).Select(value => value.ToString("x")));
            if (PrimaryAliases.TryGetValue(artwork, out var primaryAlias)) name = primaryAlias;
            var keywords = displayName.Split([' ', '-', ':', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 1).Distinct().ToArray();
            values.Add(new(glyph, name, group, artwork, keywords));
        }
        return values;
    }

    private static string NormalizeName(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_",
        RegexOptions.CultureInvariant).Trim('_');
}
