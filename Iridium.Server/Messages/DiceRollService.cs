using System.Security.Cryptography;
using Iridium.Protocol;

namespace Iridium.Server.Messages;

public interface IDiceRollRandomSource
{
    int NextInclusive(int minimum, int maximum);
}

public sealed class CryptographicDiceRollRandomSource : IDiceRollRandomSource
{
    public int NextInclusive(int minimum, int maximum) => RandomNumberGenerator.GetInt32(minimum, checked(maximum + 1));
}

public sealed class DiceRollService(IDiceRollRandomSource random)
{
    public DiceRollResultDto Roll(DiceRollRequest request)
    {
        if (request.DiceCount is < DiceRollLimits.MinimumDiceCount or > DiceRollLimits.MaximumDiceCount)
            throw new ArgumentOutOfRangeException(nameof(request), "Dice count is outside the supported range.");
        if (request.Sides is < DiceRollLimits.MinimumSides or > DiceRollLimits.MaximumSides)
            throw new ArgumentOutOfRangeException(nameof(request), "Side count is outside the supported range.");

        var rolls = new int[request.DiceCount];
        var minimum = int.MaxValue;
        var maximum = int.MinValue;
        long total = 0;
        for (var index = 0; index < rolls.Length; index++)
        {
            var value = random.NextInclusive(1, request.Sides);
            if (value < 1 || value > request.Sides)
                throw new InvalidOperationException("The dice random source returned a value outside the requested range.");
            rolls[index] = value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            total = checked(total + value);
        }
        return new(request.DiceCount, request.Sides, rolls, minimum, maximum, total);
    }

    public static string SearchableContent(DiceRollResultDto result) =>
        $"Rolled {result.DiceCount}d{result.Sides}: {string.Join(", ", result.Rolls)}. Total {result.Total}";
}
