using System.Security.Cryptography;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// PBKDF2-SHA256 password hashing with a random per-password salt. The stored string carries its
/// own parameters ("PBKDF2-SHA256$iterations$salt$hash", base64), so the iteration count can be
/// raised later without breaking existing accounts. Passwords are never stored or logged.
/// </summary>
public static class PasswordHasher
{
    public const int MinimumLength = 8;
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Returns a message when the password is not acceptable, otherwise null.</summary>
    public static string? ValidatePolicy(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinimumLength
            ? $"Password must be at least {MinimumLength} characters."
            : null;
}
