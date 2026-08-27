using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DomainEntity = CertManagement.Domain.Domain;

namespace CertManagement.Application;

public sealed class CertificateService(IDbContextFactory<CertificateDbContext> dbFactory, IHostEnvironment environment, IOptions<AcmeOptions> options, IDnsProviderFactory dnsFactory, ChallengeStore challenges, CertificateFileService files, ILogger<CertificateService> logger)
{
    private readonly AcmeOptions _options = options.Value;

    public async Task<Certificate> RequestAsync(int domainId, bool renewal = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.Include(x => x.DnsProviderAccount).SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
        var names = DomainService.Names(domain);
        if (!domain.Enabled && !renewal) throw new InvalidOperationException("域名已禁用。");
        if (names.Any(x => x.StartsWith("*.", StringComparison.Ordinal)) && domain.ChallengeType != ChallengeType.Dns01) throw new InvalidOperationException("通配符域名必须使用 DNS-01。");
        if (domain.ChallengeType == ChallengeType.Dns01 && domain.DnsProviderAccount is null) throw new InvalidOperationException("DNS-01 未配置 Provider。");
        var cert = await db.Certificates.Where(x => x.DomainId == domainId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (cert is null) { cert = new Certificate { DomainId = domainId, PrimaryDomain = domain.PrimaryDomain, Sans = domain.Sans, AutoRenew = domain.AutoRenew }; db.Certificates.Add(cert); await db.SaveChangesAsync(cancellationToken); }
        var busy = renewal ? CertificateStatus.Renewing : CertificateStatus.CreatingOrder;
        var changed = renewal
            ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Certificates SET Status = {busy.ToString()}, ErrorMessage = NULL, UpdatedAt = {DateTime.UtcNow} WHERE Id = {cert.Id} AND Status = {CertificateStatus.Issued.ToString()}", cancellationToken)
            : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Certificates SET Status = {busy.ToString()}, ErrorMessage = NULL, UpdatedAt = {DateTime.UtcNow} WHERE Id = {cert.Id} AND (Status = {CertificateStatus.Pending.ToString()} OR Status = {CertificateStatus.Failed.ToString()})", cancellationToken);
        if (changed != 1) throw new InvalidOperationException("该证书正在被另一实例处理，或当前状态不允许申请。");
        await LogAsync(cert.Id, busy, renewal ? "自动续期开始" : "申请开始", cancellationToken);
        var dnsTxtCleanupMessages = new List<string>();
        try
        {
            var acme = await CreateContextAsync(cancellationToken); var order = await acme.NewOrder(names); await SetStatusAsync(cert.Id, CertificateStatus.CreatingChallenge, "ACME Order 创建成功", cancellationToken);
            await using (var orderDb = await dbFactory.CreateDbContextAsync(cancellationToken)) { orderDb.CertificateOrders.Add(new CertificateOrder { CertificateId = cert.Id, OrderUrl = order.Location?.ToString() ?? string.Empty }); await orderDb.SaveChangesAsync(cancellationToken); }
            foreach (var name in names) await ValidateAsync(cert.Id, acme, order, name, domain, dnsTxtCleanupMessages, cancellationToken);
            await SetStatusAsync(cert.Id, CertificateStatus.Issuing, "ACME 验证成功，开始签发", cancellationToken);
            var key = KeyFactory.NewKey(KeyAlgorithm.RS256); var generated = await order.Generate(new CsrInfo { CommonName = domain.PrimaryDomain }, key); var leaf = generated.Certificate.ToPem(); var chain = string.Concat(generated.Issuers.Select(issuer => issuer.ToPem())); var full = leaf + chain;
            await using var saveDb = await dbFactory.CreateDbContextAsync(cancellationToken); var saved = await saveDb.Certificates.SingleAsync(x => x.Id == cert.Id, cancellationToken); await files.SaveAsync(saved, leaf, chain, full, key.ToPem(), cancellationToken);
            using var x509 = X509Certificate2.CreateFromPem(leaf, key.ToPem()); saved.IssuedAt = DateTime.UtcNow; saved.ExpiresAt = x509.NotAfter.ToUniversalTime(); saved.LastRenewAt = renewal ? DateTime.UtcNow : null; saved.NextRenewAt = saved.ExpiresAt.Value.AddDays(-30); saved.Status = CertificateStatus.Issued; saved.UpdatedAt = DateTime.UtcNow; await saveDb.SaveChangesAsync(cancellationToken); await LogAsync(saved.Id, saved.Status, renewal ? "自动续期签发成功" : "证书签发成功", cancellationToken); await LogDnsTxtCleanupAsync(saved.Id, CertificateStatus.Issued, dnsTxtCleanupMessages, cancellationToken); return saved;
        }
        catch (Exception ex) { var error = SafeError(ex); logger.LogError(ex, "证书 {CertificateId} 处理失败：{Error}", cert.Id, error); await FailAsync(cert.Id, error, cancellationToken); await LogDnsTxtCleanupAsync(cert.Id, CertificateStatus.Failed, dnsTxtCleanupMessages, cancellationToken); throw; }
    }

    public async Task<List<Certificate>> ListAsync(CancellationToken cancellationToken = default) { await using var db = await dbFactory.CreateDbContextAsync(cancellationToken); return await db.Certificates.Include(x => x.Domain).OrderByDescending(x => x.Id).ToListAsync(cancellationToken); }
    public async Task<Certificate?> GetCertificateAsync(int id, CancellationToken cancellationToken = default) { await using var db = await dbFactory.CreateDbContextAsync(cancellationToken); return await db.Certificates.Include(x => x.Domain).SingleOrDefaultAsync(x => x.Id == id, cancellationToken); }
    public async Task<List<CertificateLog>> LogsAsync(int id, CancellationToken cancellationToken = default) { await using var db = await dbFactory.CreateDbContextAsync(cancellationToken); return await db.CertificateLogs.Where(x => x.CertificateId == id).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken); }

    private async Task ValidateAsync(int id, AcmeContext acme, IOrderContext order, string name, DomainEntity domain, List<string> dnsTxtCleanupMessages, CancellationToken cancellationToken)
    {
        var authz = await order.Authorization(name); var challenge = domain.ChallengeType == ChallengeType.Http01 ? await authz.Http() : await authz.Dns();
        DnsTxtRecord? record = null; IDnsProvider? provider = null;
        if (domain.ChallengeType == ChallengeType.Http01) challenges.Set(challenge.Token, challenge.KeyAuthz, TimeSpan.FromMinutes(10));
        else { var baseName = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name; record = new DnsTxtRecord("_acme-challenge." + baseName, acme.AccountKey.DnsTxt(challenge.Token)); provider = dnsFactory.Create(domain.DnsProviderAccount!); await provider.CreateTxtRecordAsync(record, cancellationToken); await SetStatusAsync(id, CertificateStatus.WaitingDns, "DNS TXT 已创建，等待传播", cancellationToken); }
        try { if (record is not null) { await WaitDnsAsync(record.Name, record.Value, cancellationToken); await SetStatusAsync(id, CertificateStatus.Validating, "DNS TXT 已生效，开始 ACME 验证", cancellationToken); } else { await SetStatusAsync(id, CertificateStatus.Validating, "HTTP-01 Challenge 已创建", cancellationToken); } await challenge.Validate(); await WaitChallengeAsync(challenge, cancellationToken); }
        finally { if (domain.ChallengeType == ChallengeType.Http01) challenges.Remove(challenge.Token); if (provider is not null && record is not null) { try { await provider.DeleteTxtRecordAsync(record, cancellationToken); dnsTxtCleanupMessages.Add("DNS TXT 已清理"); } catch (Exception ex) { logger.LogWarning("DNS TXT 清理失败：{Error}", SafeError(ex)); } } }
    }

    private static async Task WaitChallengeAsync(IChallengeContext challenge, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 60; i++) { var resource = await challenge.Resource(); if (resource.Status == ChallengeStatus.Valid) return; if (resource.Status == ChallengeStatus.Invalid) throw new InvalidOperationException(string.IsNullOrWhiteSpace(resource.Error?.Detail) ? "ACME Challenge 验证失败。" : $"ACME Challenge 验证失败：{resource.Error.Detail}"); await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
        throw new TimeoutException("ACME Challenge 验证超时。");
    }
    private async Task WaitDnsAsync(string name, string expected, CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(_options.DnsPropagationTimeoutSeconds); using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (DateTimeOffset.UtcNow < end) { using var request = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type=TXT"); request.Headers.Accept.ParseAdd("application/dns-json"); using var response = await http.SendAsync(request, cancellationToken); if (response.IsSuccessStatusCode) { using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); if (json.RootElement.TryGetProperty("Answer", out var answers) && answers.EnumerateArray().Select(x => x.GetProperty("data").GetString()?.Trim('"')).Any(x => x == expected)) return; } await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.DnsPollingIntervalSeconds)), cancellationToken); }
        throw new TimeoutException("DNS TXT 记录在配置的超时时间内未生效。");
    }
    private async Task<AcmeContext> CreateContextAsync(CancellationToken cancellationToken)
    {
        var server = string.Equals(_options.Environment, "Staging", StringComparison.OrdinalIgnoreCase) ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2; var path = Path.Combine(environment.ContentRootPath, _options.AccountKeyPath); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); AcmeContext context;
        if (File.Exists(path)) context = new AcmeContext(server, KeyFactory.FromPem(await File.ReadAllTextAsync(path, cancellationToken))); else { context = new AcmeContext(server); await context.NewAccount(_options.Email, true); await File.WriteAllTextAsync(path, context.AccountKey.ToPem(), cancellationToken); }
        return context;
    }
    private async Task SetStatusAsync(int id, CertificateStatus status, string message, CancellationToken cancellationToken) { await using var db = await dbFactory.CreateDbContextAsync(cancellationToken); var item = await db.Certificates.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("证书不存在。"); item.Status = status; item.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(cancellationToken); await LogAsync(id, status, message, cancellationToken); }
    private async Task LogDnsTxtCleanupAsync(int id, CertificateStatus terminalStatus, List<string> cleanupMessages, CancellationToken cancellationToken) { foreach (var message in cleanupMessages) await LogAsync(id, terminalStatus, message, cancellationToken); }
    private async Task FailAsync(int id, string error, CancellationToken cancellationToken) { await using var db = await dbFactory.CreateDbContextAsync(cancellationToken); var item = await db.Certificates.FindAsync([id], cancellationToken); if (item is null) return; item.Status = CertificateStatus.Failed; item.ErrorMessage = error; item.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(cancellationToken); await LogAsync(id, CertificateStatus.Failed, error, cancellationToken); }
    private async Task LogAsync(int id, CertificateStatus status, string message, CancellationToken cancellationToken) { await using var db = await dbFactory.CreateDbContextAsync(cancellationToken); db.CertificateLogs.Add(new CertificateLog { CertificateId = id, Status = status, Message = message }); await db.SaveChangesAsync(cancellationToken); logger.LogInformation("证书 {CertificateId}：{Message}", id, message); }
    private static string FirstCertificate(string pem) { const string end = "-----END CERTIFICATE-----"; var i = pem.IndexOf(end, StringComparison.Ordinal); return i < 0 ? pem : pem[..(i + end.Length)] + Environment.NewLine; }
    private static string SafeError(Exception ex) => ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
}
