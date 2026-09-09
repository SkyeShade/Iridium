using System.Globalization;

namespace Iridium.Protocol;

public static class DiceRollLimits
{
    public const int MinimumDiceCount = 1;
    public const int MaximumDiceCount = 100;
    public const int MinimumSides = 2;
    public const int MaximumSides = 1_000_000;
}

public sealed record DiceRollRequest(int DiceCount, int Sides);

public sealed record DiceRollResultDto(
    int DiceCount,
    int Sides,
    IReadOnlyList<int> Rolls,
    int Minimum,
    int Maximum,
    long Total);

public sealed record DiceRollCommandParseResult(bool IsCommand, DiceRollRequest? Request = null, string? Error = null)
{
    public bool IsValid => IsCommand && Request is not null && Error is null;
}

public static class DiceRollCommandParser
{
    public const string UsageMessage = "Use /roll XdY, for example /roll 2d20.";

    public static DiceRollCommandParseResult Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new(false);
        var value = content.Trim();
        if (!value.StartsWith("/roll", StringComparison.OrdinalIgnoreCase)) return new(false);
        if (value.Length > 5 && !char.IsWhiteSpace(value[5])) return new(false);

        var notation = value[5..].Trim();
        if (notation.Length == 0) return Invalid(UsageMessage);
        var separator = notation.IndexOfAny(['d', 'D']);
        if (separator <= 0 || separator != notation.LastIndexOfAny(['d', 'D']) || separator == notation.Length - 1)
            return Invalid(UsageMessage);
        var countText = notation[..separator];
        var sidesText = notation[(separator + 1)..];
        if (!AsciiDigits(countText) || !AsciiDigits(sidesText) ||
            !int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var diceCount) ||
            !int.TryParse(sidesText, NumberStyles.None, CultureInfo.InvariantCulture, out var sides))
            return Invalid(UsageMessage);

        if (diceCount < DiceRollLimits.MinimumDiceCount) return Invalid("Roll at least one die.");
        if (diceCount > DiceRollLimits.MaximumDiceCount)
            return Invalid($"You can roll at most {DiceRollLimits.MaximumDiceCount} dice at once.");
        if (sides < DiceRollLimits.MinimumSides)
            return Invalid($"Dice must have at least {DiceRollLimits.MinimumSides} sides.");
        if (sides > DiceRollLimits.MaximumSides)
            return Invalid($"Dice may have at most {DiceRollLimits.MaximumSides.ToString("N0", CultureInfo.InvariantCulture)} sides.");
        return new(true, new(diceCount, sides));
    }

    private static bool AsciiDigits(string value) => value.Length > 0 && value.All(character => character is >= '0' and <= '9');
    private static DiceRollCommandParseResult Invalid(string message) => new(true, Error: message);
}
