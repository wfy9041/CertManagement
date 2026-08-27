using System.Security.Cryptography;
using CertManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CertManagement.Application;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = string.Empty;
}

public sealed class AdminCredentialService(IOptions<AdminOptions> options, IDbContextFactory<CertificateDbContext>? dbFactory = null)
{
    private readonly AdminOptions _options = options.Value;

    public bool Verify(string username, string password) => string.Equals(username, _options.Username, StringComparison.Ordinal) && VerifyHash(password, _options.PasswordHash);

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var credential = await db.AdminCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Username == username, cancellationToken);
        return credential is not null ? VerifyHash(password, credential.PasswordHash) : string.Equals(username, _options.Username, StringComparison.Ordinal) && VerifyHash(password, _options.PasswordHash);
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string username, string oldPassword, string newPassword, string confirmPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8) return (false, "新密码至少需要 8 位。");
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal)) return (false, "两次输入的新密码不一致。");
        if (string.Equals(oldPassword, newPassword, StringComparison.Ordinal)) return (false, "新密码不能与旧密码相同。");
        if (!await VerifyAsync(username, oldPassword, cancellationToken)) return (false, "旧密码错误。");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var credential = await db.AdminCredentials.SingleOrDefaultAsync(x => x.Username == username, cancellationToken);
        if (credential is null)
        {
            credential = new() { Username = username };
            db.AdminCredentials.Add(credential);
        }
        credential.PasswordHash = Hash(newPassword);
        credential.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static bool VerifyHash(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash)) return false;
        var parts = passwordHash.Split('.', 3);
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
