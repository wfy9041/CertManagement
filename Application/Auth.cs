using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CertManagement.Application;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = string.Empty;
}

public sealed class AdminCredentialService(IOptions<AdminOptions> options)
{
    private readonly AdminOptions _options = options.Value;

    public bool Verify(string username, string password)
    {
        if (!string.Equals(username, _options.Username, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_options.PasswordHash)) return false;
        var parts = _options.PasswordHash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16); const int iterations = 210_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
}
