using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace CertManagement.Application;

using WafDeploymentTarget = CertManagement.Domain.DeploymentTarget;

public sealed class WafDeploymentService(IDbContextFactory<CertificateDbContext> dbFactory, CertificateFileService files, IHttpClientFactory clients, IDataProtectionProvider protection, ILogger<WafDeploymentService> logger)
{
	private readonly IDataProtector protector = protection.CreateProtector("AliyunWafSecrets.v1");
	private static readonly ConcurrentDictionary<int, SemaphoreSlim> DeploymentLocks = new();

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

	public async Task<WafDeploymentTarget> SaveTargetAsync(WafDeploymentTarget input, string secret, CancellationToken ct = default, int credentialSourceId = 0)
	{
		if (input.DomainId == 0 || string.IsNullOrWhiteSpace(input.Name) || (credentialSourceId == 0 && string.IsNullOrWhiteSpace(input.AccessKeyId)) || string.IsNullOrWhiteSpace(input.InstanceId) || string.IsNullOrWhiteSpace(input.WafDomain))
			throw new ArgumentException("所属域名、名称、AccessKeyId、InstanceId / SiteId 和目标域名不能为空。");
		if (input.Type is not (DeploymentTargetType.Waf or DeploymentTargetType.Esa))
			throw new ArgumentException("不支持的部署目标类型。");
		if (input.Type == DeploymentTargetType.Esa && (!long.TryParse(input.InstanceId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var siteId) || siteId <= 0))
			throw new ArgumentException("ESA SiteId 必须为正整数。");
		var endpoint = string.IsNullOrWhiteSpace(input.Endpoint) ? (input.Type == DeploymentTargetType.Esa ? EsaDeploymentClient.DefaultEndpoint : "wafopenapi.cn-hangzhou.aliyuncs.com") : input.Endpoint.Trim();
		if (input.Type == DeploymentTargetType.Esa) endpoint = EsaDeploymentClient.NormalizeEndpoint(endpoint);
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = input.Id == 0 ? new WafDeploymentTarget() : await db.WafDeploymentTargets.FindAsync([input.Id], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		if (!await db.Domains.AnyAsync(x => x.Id == input.DomainId, ct))
			throw new ArgumentException("所属域名不存在。");
		var accessKeyId = input.AccessKeyId?.Trim() ?? string.Empty;
		if (credentialSourceId != 0)
		{
			var source = await db.WafDeploymentTargets.AsNoTracking().SingleOrDefaultAsync(x =>
				x.Id == credentialSourceId && x.Type == input.Type &&
				x.AccessKeyId != "" && x.ProtectedAccessKeySecret != "", ct)
				?? throw new ArgumentException("所选部署凭据已不可用，请重新选择。");
			accessKeyId = source.AccessKeyId;
			target.ProtectedAccessKeySecret = source.ProtectedAccessKeySecret;
		}
		else if (input.Type == DeploymentTargetType.Esa && string.IsNullOrWhiteSpace(secret) && (string.IsNullOrWhiteSpace(target.ProtectedAccessKeySecret) || target.Type != input.Type || target.AccessKeyId != accessKeyId))
			throw new ArgumentException("新增目标或切换类型、AccessKeyId 时必须填写 AccessKeySecret。");
		if (target.Type != input.Type || target.InstanceId != input.InstanceId.Trim() || target.AccessKeyId != accessKeyId || target.Endpoint != endpoint)
			target.EsaCertificateId = null;
		if (input.Id == 0)
			db.WafDeploymentTargets.Add(target);
		target.DomainId = input.DomainId;
		target.Type = input.Type;
		target.Name = input.Name.Trim();
		target.AccessKeyId = accessKeyId;
		target.InstanceId = input.InstanceId.Trim();
		target.WafDomain = input.WafDomain.Trim();
		target.Endpoint = endpoint;
		target.Enabled = input.Enabled;
		target.UpdatedAt = DateTime.UtcNow;
		if (credentialSourceId == 0 && !string.IsNullOrWhiteSpace(secret))
			target.ProtectedAccessKeySecret = protector.Protect(secret.Trim());
		await db.SaveChangesAsync(ct);
		return target;
	}

	public async Task<bool> TestAsync(int id, CancellationToken ct = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(ct);
		var target = await db.WafDeploymentTargets.FindAsync([id], ct) ?? throw new KeyNotFoundException("WAF 部署目标不存在。");
		if (target.Type == DeploymentTargetType.Esa)
		{
			await CreateEsaClient(target).ValidateSiteAsync(target, target.TargetDomain, ct);
			return true;
		}
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
		if (target.Type == DeploymentTargetType.Esa)
		{
			await CreateEsaClient(target).ValidateSiteAsync(target, wafDomain, ct);
			return;
		}
		var json = await CallAsync(target, "DescribeDomain", new Dictionary<string, string> { ["Domain"] = wafDomain }, ct);
		if (!json.RootElement.TryGetProperty("Domain", out _))
			throw new InvalidOperationException($"WAF 实例中未找到域名：{wafDomain}");
	}

	public async Task DeployAsync(int certificateId, int? targetId = null, CancellationToken ct = default)
	{
		var gate = CertificateService.OperationLock(certificateId);
		await gate.WaitAsync(ct);
		try { await DeployCoreAsync(certificateId, targetId, ct); }
		finally { gate.Release(); }
	}

	internal async Task DeployCoreAsync(int certificateId, int? targetId = null, CancellationToken ct = default)
	{
		await using var read = await dbFactory.CreateDbContextAsync(ct);
		var cert = await read.Certificates.Include(x => x.Domain).ThenInclude(x => x.DeploymentTargets).SingleOrDefaultAsync(x => x.Id == certificateId, ct);
		if (cert is null)
			return;
		var targets = cert.Domain.DeploymentTargets.Where(x => x.Enabled && (x.Type == DeploymentTargetType.Waf || x.Type == DeploymentTargetType.Esa) && (targetId is null || x.Id == targetId.Value)).ToList();
		if (targets.Count == 0)
			return;
		var certificate = await files.ReadPemAsync(certificateId, "fullchain", ct);
		var key = await files.ReadPemAsync(certificateId, "privkey", ct);
		foreach (var candidate in targets)
		{
			var gate = DeploymentLocks.GetOrAdd(candidate.Id, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(ct);
			try
			{
				await using var db = await dbFactory.CreateDbContextAsync(ct);
				var target = await db.WafDeploymentTargets.SingleAsync(x => x.Id == candidate.Id, ct);
				if (!target.Enabled || target.DomainId != cert.DomainId) continue;
				var item = new CertificateDeployment { CertificateId = certificateId, WafDeploymentTargetId = target.Id, WafDomain = target.WafDomain, Status = WafDeploymentStatus.Running };
				db.CertificateDeployments.Add(item);
				await db.SaveChangesAsync(ct);
				try
				{
					JsonDocument json;
					if (target.Type == DeploymentTargetType.Esa)
					{
						using var leaf = X509Certificate2.CreateFromPem(certificate.Content);
						var hostname = target.TargetDomain.Trim().TrimEnd('.');
						var san = leaf.Extensions["2.5.29.17"];
						var covered = hostname.StartsWith("*.", StringComparison.Ordinal)
							? san is not null && new X509SubjectAlternativeNameExtension(san.RawData, san.Critical).EnumerateDnsNames().Any(x => string.Equals(x, hostname, StringComparison.OrdinalIgnoreCase))
							: leaf.MatchesHostname(hostname, allowWildcards: true, allowCommonName: false);
						if (!covered) throw new InvalidOperationException("当前证书未覆盖 ESA 目标域名，请检查证书 SAN 和部署目标。");
						var esa = CreateEsaClient(target);
						await esa.ValidateSiteAsync(target, target.TargetDomain, ct);
						json = await esa.DeployAsync(target, certificate.Content, key.Content, ct);
					}
					else
						json = await CallAsync(target, "CreateCertificate", new Dictionary<string, string> { ["Domain"] = target.WafDomain, ["Certificate"] = certificate.Content, ["PrivateKey"] = key.Content, ["CertificateName"] = $"{target.WafDomain}-{DateTime.UtcNow:yyyyMMddHHmmss}" }, ct);
					using (json)
					{
						if (target.Type == DeploymentTargetType.Esa)
						{
							var esaId = json.RootElement.GetProperty("Id").GetString();
							if (string.IsNullOrWhiteSpace(esaId)) throw new InvalidOperationException("ESA 未返回证书 ID，请核对云端结果。");
							target.EsaCertificateId = esaId;
							item.EsaCertificateId = esaId;
							target.UpdatedAt = DateTime.UtcNow;
						}
						else
							item.WafCertificateId = ReadInt64(json.RootElement, "CertificateId");
						item.Status = WafDeploymentStatus.Succeeded;
						item.RequestId = json.RootElement.TryGetProperty("RequestId", out var requestId) ? requestId.GetString() : null;
						item.CompletedAt = DateTime.UtcNow;
					}
				}
				catch (Exception ex) { item.Status = WafDeploymentStatus.Failed; item.ErrorMessage = SafeError(ex); item.CompletedAt = DateTime.UtcNow; logger.LogError(ex, "证书部署失败，证书 {CertificateId}，目标 {TargetId}", certificateId, target.Id); }
				await db.SaveChangesAsync(ct);
			}
			finally { gate.Release(); }
		}
	}

	private EsaDeploymentClient CreateEsaClient(WafDeploymentTarget target) => new(clients.CreateClient(), target.AccessKeyId, protector.Unprotect(target.ProtectedAccessKeySecret), target.Endpoint);

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
