using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CertManagement.Application;

using WafDeploymentTarget = CertManagement.Domain.DeploymentTarget;

public sealed class WafDeploymentService(IDbContextFactory<CertificateDbContext> dbFactory, CertificateFileService files, IHttpClientFactory clients, IDataProtectionProvider protection, ILogger<WafDeploymentService> logger)
{
	private readonly IDataProtector protector = protection.CreateProtector("AliyunWafSecrets.v1");

	public async Task<List<WafDeploymentTarget>> ListTargetsAsync(CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		return await db.WafDeploymentTargets.Include(x => x.Domain).OrderBy(x => x.Name).ToListAsync(ct);
	}

	public async Task<List<CertificateDeployment>> ListDeploymentsAsync(int? beforeId = null, int take = 30, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		return await db.CertificateDeployments.Include(x => x.Certificate).Include(x => x.DeploymentTarget).Where(x => beforeId == null || x.Id < beforeId.Value).OrderByDescending(x => x.Id).Take(take).ToListAsync(ct);
	}

	public async Task<WafDeploymentTarget> SaveTargetAsync(WafDeploymentTarget input, string secret, CancellationToken ct = default)
	{
		if (input.DomainId == 0 || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.AccessKeyId) || string.IsNullOrWhiteSpace(input.InstanceId) || string.IsNullOrWhiteSpace(input.WafDomain))
			throw new ArgumentException("所属域名、WAF 名称、AccessKeyId、InstanceId 和 WAF 域名不能为空。");
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = input.Id == 0 ? new WafDeploymentTarget() : await db.WafDeploymentTargets.FindAsync([input.Id], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		if (input.Id == 0)
			db.WafDeploymentTargets.Add(target);
		target.DomainId = input.DomainId;
		target.Type = DeploymentTargetType.Waf;
		target.Name = input.Name.Trim();
		target.AccessKeyId = input.AccessKeyId.Trim();
		target.InstanceId = input.InstanceId.Trim();
		target.WafDomain = input.WafDomain.Trim();
		target.Endpoint = string.IsNullOrWhiteSpace(input.Endpoint) ? "wafopenapi.cn-hangzhou.aliyuncs.com" : input.Endpoint.Trim();
		target.Enabled = input.Enabled;
		target.UpdatedAt = DateTime.UtcNow;
		if (!string.IsNullOrWhiteSpace(secret))
			target.ProtectedAccessKeySecret = protector.Protect(secret.Trim());
		await db.SaveChangesAsync(ct);
		return target;
	}

	public async Task<bool> TestAsync(int id, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = await db.WafDeploymentTargets.FindAsync([id], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		await CallAsync(target, "DescribeInstanceInfo", new Dictionary<string, string>(), ct);
		return true;
	}

	public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = await db.WafDeploymentTargets.FindAsync([id], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		target.Enabled = enabled;
		target.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(ct);
	}

	public async Task SetDomainTargetAsync(int domainId, int? targetId, string? wafDomain, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var domain = await db.Domains.FindAsync([domainId], ct) ?? throw new KeyNotFoundException("域名不存在。");
		WafDeploymentTarget? target = null;
		if (targetId is not null)
			target = await db.WafDeploymentTargets.FindAsync([targetId.Value], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		if (target is not null)
		{
			var resolvedDomain = string.IsNullOrWhiteSpace(wafDomain) ? domain.PrimaryDomain : wafDomain.Trim();
			await ValidateDomainAsync(target, resolvedDomain, ct);
			target.DomainId = domainId;
			target.WafDomain = resolvedDomain;
			target.UpdatedAt = DateTime.UtcNow;
		}
		await db.SaveChangesAsync(ct);
	}

	public async Task SetTargetDomainsAsync(int targetId, IReadOnlyCollection<int> domainIds, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = await db.WafDeploymentTargets.FindAsync([targetId], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		var domains = await db.Domains.Where(x => domainIds.Contains(x.Id)).ToListAsync(ct);
		foreach (var domain in domains)
		{
			await ValidateDomainAsync(target, target.WafDomain, ct);
			target.DomainId = domain.Id;
			target.UpdatedAt = DateTime.UtcNow;
		}
		await db.SaveChangesAsync(ct);
	}

	public async Task ValidateDomainAsync(int targetId, string wafDomain, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = await db.WafDeploymentTargets.FindAsync([targetId], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		await ValidateDomainAsync(target, wafDomain, ct);
	}

	private async Task ValidateDomainAsync(WafDeploymentTarget target, string wafDomain, CancellationToken ct)
	{
		var json = await CallAsync(target, "DescribeDomain", new Dictionary<string, string> { ["Domain"] = wafDomain }, ct);
		if (!json.RootElement.TryGetProperty("Domain", out _))
			throw new InvalidOperationException($"WAF 实例中未找到域名：{wafDomain}");
	}

	public async Task DeployAsync(int certificateId, int? targetId = null, CancellationToken ct = default)
	{
		await using var read = await dbFactory.CreateDbContextAsync(ct);
		var cert = await read.Certificates.Include(x => x.Domain).ThenInclude(x => x.DeploymentTargets).SingleOrDefaultAsync(x => x.Id == certificateId, ct);
		if (cert is null)
			return;
		var targets = cert.Domain.DeploymentTargets.Where(x => x.Enabled && x.Type == DeploymentTargetType.Waf && (targetId is null || x.Id == targetId.Value)).ToList();
		if (targets.Count == 0)
			return;
		var certificate = await files.ReadPemAsync(certificateId, "fullchain", ct);
		var key = await files.ReadPemAsync(certificateId, "privkey", ct);
		foreach (var target in targets)
		{
			var item = new CertificateDeployment { CertificateId = certificateId, WafDeploymentTargetId = target.Id, WafDomain = target.WafDomain, Status = WafDeploymentStatus.Running };
			await using var db = await dbFactory.CreateDbContextAsync(ct);
			db.CertificateDeployments.Add(item);
			await db.SaveChangesAsync(ct);
			try
			{
				var json = await CallAsync(target, "CreateCertificate", new Dictionary<string, string> { ["Domain"] = target.WafDomain, ["Certificate"] = certificate.Content, ["PrivateKey"] = key.Content, ["CertificateName"] = $"{target.WafDomain}-{DateTime.UtcNow:yyyyMMddHHmmss}" }, ct);
				item.Status = WafDeploymentStatus.Succeeded;
				item.WafCertificateId = ReadInt64(json.RootElement, "CertificateId");
				item.RequestId = json.RootElement.TryGetProperty("RequestId", out var requestId) ? requestId.GetString() : null;
				item.CompletedAt = DateTime.UtcNow;
			}
			catch (Exception ex) { item.Status = WafDeploymentStatus.Failed; item.ErrorMessage = SafeError(ex); item.CompletedAt = DateTime.UtcNow; logger.LogError(ex, "WAF 证书部署失败，证书 {CertificateId}，目标 {TargetId}", certificateId, target.Id); }
			await db.SaveChangesAsync(ct);
		}
	}

	private async Task<JsonDocument> CallAsync(WafDeploymentTarget target, string action, IReadOnlyDictionary<string, string> extra, CancellationToken ct)
	{
		var secret = protector.Unprotect(target.ProtectedAccessKeySecret);
		var query = new Dictionary<string, string>(extra) { ["AccessKeyId"] = target.AccessKeyId, ["Action"] = action, ["Format"] = "JSON", ["SignatureMethod"] = "HMAC-SHA1", ["SignatureNonce"] = Guid.NewGuid().ToString("N"), ["SignatureVersion"] = "1.0", ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["Version"] = "2019-09-10", ["InstanceId"] = target.InstanceId };
		var canonical = string.Join('&', query.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Encode(x.Key) + "=" + Encode(x.Value)));
		var signText = "GET&%2F&" + Encode(canonical);
		using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret + "&"));
		query["Signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signText)));
		using var response = await clients.CreateClient().GetAsync("https://" + target.Endpoint.TrimEnd('/') + "/?" + string.Join('&', query.Select(x => Encode(x.Key) + "=" + Encode(x.Value))), ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
			throw new HttpRequestException($"WAF API 返回 {(int)response.StatusCode}：{body[..Math.Min(300, body.Length)]}");
		var json = JsonDocument.Parse(body);
		if (json.RootElement.TryGetProperty("Code", out var code) && !string.IsNullOrWhiteSpace(code.GetString()))
			throw new InvalidOperationException($"阿里云 WAF API 返回错误：{code.GetString()}");
		return json;
	}

	private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

	private static long? ReadInt64(JsonElement root, string propertyName)
	{
		if (!root.TryGetProperty(propertyName, out var value))
			return null;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
			return number;
		return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) ? text : null;
	}

	private static string SafeError(Exception ex) => ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
}