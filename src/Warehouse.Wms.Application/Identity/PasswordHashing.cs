using System.Globalization;
using System.Security.Cryptography;

namespace Warehouse.Wms.Application.Identity;

public static class PasswordHashing
{
    public const string Scheme = "pbkdf2-sha256";
    public const int CurrentVersion = 1;
    public const int CurrentIterations = 120_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 1_000_000;

    public static string Hash(string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, CurrentIterations, HashAlgorithmName.SHA256, HashLength);
        return $"{Scheme}$v{CurrentVersion}$sha256${CurrentIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded, out bool needsRehash)
    {
        needsRehash = false;
        try
        {
            var fields = encoded.Split('$');
            if (fields.Length != 6 || fields[0] != Scheme || fields[1] != $"v{CurrentVersion}" || fields[2] != "sha256"
                || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
                || iterations < MinIterations || iterations > MaxIterations)
                return false;

            var salt = Convert.FromBase64String(fields[4]);
            var expected = Convert.FromBase64String(fields[5]);
            if (salt.Length != SaltLength || expected.Length != HashLength) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashLength);
            needsRehash = iterations != CurrentIterations;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must contain at least 8 characters.", nameof(password));
    }
}
