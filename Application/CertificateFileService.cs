using System.Security.Cryptography.X509Certificates;
using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CertManagement.Application;

public sealed class CertificateFileService(IWebHostEnvironment environment, IDbContextFactory<CertificateDbContext> dbFactory)
{
    private string Root => Path.GetFullPath(Path.Combine(environment.ContentRootPath, "data", "certificates"));

    public async Task SaveAsync(Certificate certificate, string certificatePem, string chainPem, string fullChainPem, string privateKeyPem, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Root, certificate.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "cert.pem"), certificatePem, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "chain.pem"), chainPem, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "fullchain.pem"), fullChainPem, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "privkey.pem"), privateKeyPem, cancellationToken);
        certificate.CertificatePath = Relative(Path.Combine(directory, "cert.pem"));
        certificate.ChainPath = Relative(Path.Combine(directory, "chain.pem"));
        certificate.FullChainPath = Relative(Path.Combine(directory, "fullchain.pem"));
        certificate.PrivateKeyPath = Relative(Path.Combine(directory, "privkey.pem"));
    }

    public async Task<(string Content, string FileName, string ContentType)> ReadPemAsync(int certificateId, string kind, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var certificate = await db.Certificates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == certificateId, cancellationToken) ?? throw new KeyNotFoundException("证书不存在。");
        var relative = kind switch { "cert" => certificate.CertificatePath, "chain" => certificate.ChainPath, "fullchain" => certificate.FullChainPath, "privkey" => certificate.PrivateKeyPath, _ => null };
        if (relative is null) throw new FileNotFoundException("证书文件不存在。");
        var path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("非法证书路径。");
        return (await File.ReadAllTextAsync(path, cancellationToken), $"{kind}.pem", "application/x-pem-file");
    }

    public async Task<(byte[] Content, string FileName)> CreatePfxAsync(int certificateId, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("PFX 密码不能为空。", nameof(password));
        var key = await ReadPemAsync(certificateId, "privkey", cancellationToken);
        var fullChain = await ReadPemAsync(certificateId, "fullchain", cancellationToken);
        using var certificate = X509Certificate2.CreateFromPem(fullChain.Content, key.Content);
        return (certificate.Export(X509ContentType.Pkcs12, password), $"certificate-{certificateId}.pfx");
    }

    private string Relative(string path) => Path.GetRelativePath(environment.ContentRootPath, path);
}
