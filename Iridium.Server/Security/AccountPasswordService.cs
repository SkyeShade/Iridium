using System.Globalization;
using System.Security.Cryptography;
using Iridium.Server.Domain;
using Microsoft.AspNetCore.Identity;

namespace Iridium.Server.Security;

public enum AccountPasswordVerificationResult
{
    Failed,
    Success,
    SuccessRehashNeeded
}

public interface IAccountPasswordService
{
    string HashPassword(NodeAccount account, string password);
    AccountPasswordVerificationResult VerifyPassword(NodeAccount account, string? password);
}

public sealed class AccountPasswordService : IAccountPasswordService
{
    private const string LegacyVersion = "v1";
    private const int LegacyIterations = 210_000;
    private const int LegacySaltBytes = 16;
    private const int LegacyHashBytes = 32;
    private readonly PasswordHasher<NodeAccount> _current = new();

    public string HashPassword(NodeAccount account, string password) =>
        _current.HashPassword(account, password);

    public AccountPasswordVerificationResult VerifyPassword(NodeAccount account, string? password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(account.PasswordHash))
            return AccountPasswordVerificationResult.Failed;

        if (IsLegacyFormat(account.PasswordHash))
            return VerifyLegacy(account.PasswordHash, password)
                ? AccountPasswordVerificationResult.SuccessRehashNeeded
                : AccountPasswordVerificationResult.Failed;

        return VerifyCurrent(account, password);
    }

    private AccountPasswordVerificationResult VerifyCurrent(NodeAccount account, string password)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(account.PasswordHash);
        }
        catch (FormatException)
        {
            return AccountPasswordVerificationResult.Failed;
        }

        try
        {
            if (decoded.Length == 0 || decoded[0] is not (0x00 or 0x01))
                return AccountPasswordVerificationResult.Failed;

            var result = _current.VerifyHashedPassword(account, account.PasswordHash, password);
            return result switch
            {
                PasswordVerificationResult.Success => AccountPasswordVerificationResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded => AccountPasswordVerificationResult.SuccessRehashNeeded,
                _ => AccountPasswordVerificationResult.Failed
            };
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or IndexOutOfRangeException)
        {
            return AccountPasswordVerificationResult.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static bool IsLegacyFormat(string storedHash) =>
        storedHash.StartsWith(LegacyVersion + ".", StringComparison.Ordinal);

    private static bool VerifyLegacy(string storedHash, string password)
    {
        var parts = storedHash.Split('.', StringSplitOptions.None);
        if (parts.Length != 4 || !string.Equals(parts[0], LegacyVersion, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations != LegacyIterations)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (salt.Length != LegacySaltBytes || expected.Length != LegacyHashBytes) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA512, LegacyHashBytes);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}
