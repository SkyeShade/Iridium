using System.Globalization;
using System.Text.RegularExpressions;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record MessageSearchParseContext(
    IReadOnlyList<CommunityMemberDto> Members,
    IReadOnlyList<CommunityChannelDto> Channels,
    IReadOnlyDictionary<string, Guid>? ResolvedTokens = null,
    TimeZoneInfo? TimeZone = null);

public sealed record MessageSearchParseResult(MessageSearchQueryDto Query, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static partial class MessageSearchParser
{
    public static MessageSearchParseResult Parse(
        string input, MessageSearchParseContext context, MessageSearchSort sort = MessageSearchSort.Newest)
    {
        Guid? from = null, channel = null, mentioned = null;
        var authorType = MessageAuthorType.User;
        DateTimeOffset? before = null, after = null, duringStart = null, duringEnd = null;
        var types = new HashSet<MessageSearchContentType>();
        var errors = new List<string>();
        var zone = context.TimeZone ?? TimeZoneInfo.Local;

        var text = TokenRegex().Replace(input ?? string.Empty, match =>
        {
            var key = match.Groups["key"].Value.ToLowerInvariant();
            var value = match.Groups["value"].Value.Trim().Trim('"');
            switch (key)
            {
                case "from": from = ResolveMember(key, value, context, errors); break;
                case "mentions": mentioned = ResolveMember(key, value, context, errors); break;
                case "in": channel = ResolveChannel(value, context, errors); break;
                case "has":
                    if (Enum.TryParse<MessageSearchContentType>(value, true, out var type)) types.Add(type);
                    else errors.Add($"Unknown content type '{value}'.");
                    break;
                case "before": before = ParseBoundary(value, zone, "before", errors); break;
                case "after": after = ParseBoundary(value, zone, "after", errors); break;
                case "during":
                    if (TryParseLocal(value, zone, out var start, out var dateOnly))
                    {
                        duringStart = start;
                        duringEnd = start.AddDays(1);
                    }
                    else errors.Add($"Invalid date '{value}'.");
                    break;
                case "author":
                    if (!Enum.TryParse<MessageAuthorType>(value, true, out authorType))
                        errors.Add($"Unknown author type '{value}'.");
                    break;
            }
            return " ";
        });

        text = WhitespaceRegex().Replace(text, " ").Trim();
        return new(new(string.IsNullOrWhiteSpace(text) ? null : text, from, channel, mentioned,
            types.ToArray(), before, after, duringStart, duringEnd, authorType, sort), errors);
    }

    private static Guid? ResolveMember(string key, string value, MessageSearchParseContext context, List<string> errors)
    {
        if (context.ResolvedTokens?.TryGetValue($"{key}:{value}".ToLowerInvariant(), out var resolved) == true)
            return resolved;
        var matches = context.Members.Where(member =>
            member.Username.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            member.DisplayName.Equals(value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) return matches[0].AccountId;
        errors.Add(matches.Length == 0 ? $"User '{value}' was not found." : $"User '{value}' is ambiguous.");
        return null;
    }

    private static Guid? ResolveChannel(string value, MessageSearchParseContext context, List<string> errors)
    {
        if (context.ResolvedTokens?.TryGetValue($"in:{value}".ToLowerInvariant(), out var resolved) == true)
            return resolved;
        var channel = context.Channels.SingleOrDefault(item => item.Name.Equals(value.TrimStart('#'), StringComparison.OrdinalIgnoreCase));
        if (channel is not null) return channel.Id;
        errors.Add($"Channel '{value}' was not found.");
        return null;
    }

    private static DateTimeOffset? ParseBoundary(string value, TimeZoneInfo zone, string name, List<string> errors)
    {
        if (TryParseLocal(value, zone, out var boundary, out _)) return boundary;
        errors.Add($"Invalid {name} date '{value}'.");
        return null;
    }

    private static bool TryParseLocal(string value, TimeZoneInfo zone, out DateTimeOffset utc, out bool dateOnly)
    {
        dateOnly = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date);
        if (dateOnly)
        {
            var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            utc = new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            return true;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
        {
            utc = parsed.ToUniversalTime();
            return true;
        }
        utc = default;
        return false;
    }

    [GeneratedRegex(@"(?:^|\s)(?<key>from|in|mentions|has|before|after|during|author):(?<value>""[^""]+""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
