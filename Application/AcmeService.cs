using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DomainEntity = CertManagement.Domain.Domain;

namespace CertManagement.Application;

public sealed class CertificateService(IDbContextFactory<CertificateDbContext> dbFactory, IHostEnvironment environment, IOptions<AcmeOptions> options, IDnsProviderFactory dnsFactory, ChallengeStore challenges, CertificateFileService files, WafDeploymentService waf, ILogger<CertificateService> logger)
{
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> OperationLocks = new();
	private readonly AcmeOptions _options = options.Value;

	internal static SemaphoreSlim OperationLock(int id) => OperationLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

	public async Task<Certificate> RequestAsync(int domainId, bool renewal = false, CancellationToken cancellationToken = default)
	{
		var cert = await StartAsync(domainId, renewal, cancellationToken);
		return await ProcessAsync(cert.Id, renewal, cancellationToken);
	}

	public async Task<Certificate> StartAsync(int domainId, bool renewal = false, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		var domain = await db.Domains.Include(x => x.DnsProviderAccount).SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
		var names = DomainService.Names(domain);
		if (!domain.Enabled && !renewal)
			throw new InvalidOperationException("域名已禁用。");
		if (names.Any(x => x.StartsWith("*.", StringComparison.Ordinal)) && domain.ChallengeType != ChallengeType.Dns01)
			throw new InvalidOperationException("通配符域名必须使用 DNS-01。");
		if (domain.ChallengeType == ChallengeType.Dns01 && domain.DnsProviderAccount is null)
			throw new InvalidOperationException("DNS-01 未配置 Provider。");
		var cert = await db.Certificates.Where(x => x.DomainId == domainId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
		if (cert is null)
		{
			if (renewal)
				throw new InvalidOperationException("该域名没有已签发的证书，请先申请。");
			cert = new Certificate { DomainId = domainId, PrimaryDomain = domain.PrimaryDomain, Sans = domain.Sans, AutoRenew = domain.AutoRenew };
			db.Certificates.Add(cert);
			await db.SaveChangesAsync(cancellationToken);
		}
		var busy = renewal ? CertificateStatus.Renewing : CertificateStatus.CreatingOrder;
		var changed = renewal
			? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Certificates SET Status = {busy.ToString()}, ErrorMessage = NULL, UpdatedAt = {DateTime.UtcNow} WHERE Id = {cert.Id} AND Status = {CertificateStatus.Issued.ToString()}", cancellationToken)
			: await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Certificates SET Status = {busy.ToString()}, ErrorMessage = NULL, UpdatedAt = {DateTime.UtcNow} WHERE Id = {cert.Id} AND (Status = {CertificateStatus.Pending.ToString()} OR Status = {CertificateStatus.Failed.ToString()})", cancellationToken);
		if (changed != 1)
		{
			var currentStatus = await db.Certificates.AsNoTracking().Where(x => x.Id == cert.Id).Select(x => (CertificateStatus?)x.Status).SingleOrDefaultAsync(cancellationToken);
			if (currentStatus is CertificateStatus.CreatingOrder or CertificateStatus.CreatingChallenge or CertificateStatus.WaitingDns or CertificateStatus.Validating or CertificateStatus.Issuing or CertificateStatus.Renewing)
				throw new InvalidOperationException("该证书正在申请或续期处理中，请等待当前任务完成后再试。");
			if (!renewal && currentStatus == CertificateStatus.Issued)
				throw new InvalidOperationException("该证书已签发；如需重新签发，请使用“立即续期”。");
			if (renewal)
				throw new InvalidOperationException($"证书当前状态为“{StatusName(currentStatus)}”，仅已签发的证书可以立即续期。");
			throw new InvalidOperationException($"证书当前状态为“{StatusName(currentStatus)}”，不能发起申请。");
		}
		await transaction.CommitAsync(cancellationToken);
		await LogAsync(cert.Id, busy, renewal ? "自动续期开始" : "申请开始", cancellationToken);
		return cert;
	}

	public Task ProcessRequestAsync(int certificateId, bool renewal, CancellationToken cancellationToken = default) => ProcessAsync(certificateId, renewal, cancellationToken);

	private async Task<Certificate> ProcessAsync(int certificateId, bool renewal, CancellationToken cancellationToken)
	{
		var gate = OperationLock(certificateId);
		await gate.WaitAsync(cancellationToken);
		try { return await ProcessCoreAsync(certificateId, renewal, cancellationToken); }
		finally { gate.Release(); }
	}

	private async Task<Certificate> ProcessCoreAsync(int certificateId, bool renewal, CancellationToken cancellationToken)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var cert = await db.Certificates.SingleOrDefaultAsync(x => x.Id == certificateId, cancellationToken) ?? throw new KeyNotFoundException("证书不存在。");
		var domain = await db.Domains.Include(x => x.DnsProviderAccount).SingleOrDefaultAsync(x => x.Id == cert.DomainId, cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
		var names = DomainService.Names(domain);
		var dnsTxtCleanupMessages = new List<string>();
		IOrderContext? order = null;
		try
		{
			var acme = await CreateContextAsync(cancellationToken);
			order = await acme.NewOrder(names);
			await SetStatusAsync(cert.Id, CertificateStatus.CreatingChallenge, "ACME Order 创建成功", cancellationToken);
			await using (var orderDb = await dbFactory.CreateDbContextAsync(cancellationToken))
			{
				orderDb.CertificateOrders.Add(new CertificateOrder { CertificateId = cert.Id, OrderUrl = order.Location?.ToString() ?? string.Empty });
				await orderDb.SaveChangesAsync(cancellationToken);
			}
			foreach (var name in names)
				await ValidateAsync(cert.Id, acme, order, name, domain, dnsTxtCleanupMessages, cancellationToken);
			await SetStatusAsync(cert.Id, CertificateStatus.Issuing, "ACME 验证成功，开始签发", cancellationToken);
			var key = KeyFactory.NewKey(KeyAlgorithm.RS256);
			var generated = await order.Generate(new CsrInfo { CommonName = domain.PrimaryDomain }, key, retryCount: 24);
			var leaf = generated.Certificate.ToPem().Trim();
			var issuers = generated.Issuers.Select(issuer => issuer.ToPem().Trim()).ToArray();
			var chain = string.Join(Environment.NewLine, issuers);
			var full = string.Join(Environment.NewLine, new[] { leaf }.Concat(issuers));
			await using var saveDb = await dbFactory.CreateDbContextAsync(cancellationToken);
			var saved = await saveDb.Certificates.SingleAsync(x => x.Id == cert.Id, cancellationToken);
			await files.SaveAsync(saved, leaf, chain, full, key.ToPem(), cancellationToken);
			using var x509 = X509Certificate2.CreateFromPem(leaf, key.ToPem());
			saved.IssuedAt = DateTime.UtcNow;
			saved.ExpiresAt = x509.NotAfter.ToUniversalTime();
			saved.LastRenewAt = renewal ? DateTime.UtcNow : null;
			saved.NextRenewAt = saved.ExpiresAt.Value.AddDays(-30);
			saved.Status = CertificateStatus.Issued;
			saved.UpdatedAt = DateTime.UtcNow;
			await saveDb.SaveChangesAsync(cancellationToken);
			await LogAsync(saved.Id, saved.Status, renewal ? "自动续期签发成功" : "证书签发成功", cancellationToken);
			await LogDnsTxtCleanupAsync(saved.Id, CertificateStatus.Issued, dnsTxtCleanupMessages, cancellationToken);
			await waf.DeployCoreAsync(saved.Id, ct: cancellationToken);
			return saved;
		}
		catch (Exception ex) { var diagnostic = order is null ? null : await ReadOrderFailureAsync(order); var error = SafeError(string.IsNullOrWhiteSpace(diagnostic) ? ex.Message : $"{ex.Message}；{diagnostic}"); logger.LogError(ex, "证书 {CertificateId} 处理失败：{Error}", cert.Id, error); await FailAsync(cert.Id, error, cancellationToken); await LogDnsTxtCleanupAsync(cert.Id, CertificateStatus.Failed, dnsTxtCleanupMessages, cancellationToken); throw; }
	}

	public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
	{
		const string busyMessage = "该证书正在申请、续期或部署，任务结束后才能删除。";
		var gate = OperationLock(id);
		if (!await gate.WaitAsync(0, cancellationToken))
			throw new InvalidOperationException(busyMessage);

		try
		{
			await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
			var certificate = await db.Certificates.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("证书不存在。");
			if (certificate.Status is not (CertificateStatus.Pending or CertificateStatus.Issued or CertificateStatus.Failed))
				throw new InvalidOperationException(busyMessage);

			var deployments = db.CertificateDeployments.Where(x => x.CertificateId == id);
			if (await deployments.AnyAsync(x => x.Status == WafDeploymentStatus.Pending || x.Status == WafDeploymentStatus.Running, cancellationToken))
				throw new InvalidOperationException(busyMessage);

			await deployments.ExecuteDeleteAsync(cancellationToken);
			await db.CertificateOrders.Where(x => x.CertificateId == id).ExecuteDeleteAsync(cancellationToken);
			await db.CertificateLogs.Where(x => x.CertificateId == id).ExecuteDeleteAsync(cancellationToken);
			db.Certificates.Remove(certificate);
			await db.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
		}
		finally { gate.Release(); }
	}

	public async Task<List<Certificate>> ListAsync(CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.Certificates.Include(x => x.Domain).OrderByDescending(x => x.Id).ToListAsync(cancellationToken);
	}

	public async Task<Certificate?> GetCertificateAsync(int id, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.Certificates.Include(x => x.Domain).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
	}

	public async Task<List<CertificateLog>> LogsAsync(int id, int? beforeId = null, int take = 30, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.CertificateLogs.Where(x => x.CertificateId == id && (beforeId == null || x.Id < beforeId.Value)).OrderByDescending(x => x.Id).Take(take).ToListAsync(cancellationToken);
	}

	private async Task ValidateAsync(int id, AcmeContext acme, IOrderContext order, string name, DomainEntity domain, List<string> dnsTxtCleanupMessages, CancellationToken cancellationToken)
	{
		var authz = await order.Authorization(name);
		var challenge = domain.ChallengeType == ChallengeType.Http01 ? await authz.Http() : await authz.Dns();
		DnsTxtRecord? record = null;
		IDnsProvider? provider = null;
		if (domain.ChallengeType == ChallengeType.Http01)
			challenges.Set(challenge.Token, challenge.KeyAuthz, TimeSpan.FromMinutes(10));
		else
		{
			var baseName = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
			record = new DnsTxtRecord("_acme-challenge." + baseName, acme.AccountKey.DnsTxt(challenge.Token));
			provider = dnsFactory.Create(domain.DnsProviderAccount!);
			await provider.CreateTxtRecordAsync(record, cancellationToken);
			await SetStatusAsync(id, CertificateStatus.WaitingDns, "DNS TXT 已创建，等待传播", cancellationToken);
		}
		try
		{
			if (record is not null)
			{
				await WaitDnsAsync(record.Name, record.Value, cancellationToken);
				await SetStatusAsync(id, CertificateStatus.Validating, "DNS TXT 已生效，开始 ACME 验证", cancellationToken);
			}
			else
			{
				await SetStatusAsync(id, CertificateStatus.Validating, "HTTP-01 Challenge 已创建", cancellationToken);
			}
			await challenge.Validate();
			await WaitChallengeAsync(challenge, cancellationToken);
		}
		finally { if (domain.ChallengeType == ChallengeType.Http01) challenges.Remove(challenge.Token); if (provider is not null && record is not null) { try { await provider.DeleteTxtRecordAsync(record, cancellationToken); dnsTxtCleanupMessages.Add("DNS TXT 已清理"); } catch (Exception ex) { logger.LogWarning("DNS TXT 清理失败：{Error}", SafeError(ex)); } } }
	}

	private static async Task WaitChallengeAsync(IChallengeContext challenge, CancellationToken cancellationToken)
	{
		for (var i = 0; i < 60; i++)
		{
			var resource = await challenge.Resource();
			if (resource.Status == ChallengeStatus.Valid)
				return;
			if (resource.Status == ChallengeStatus.Invalid)
				throw new InvalidOperationException(string.IsNullOrWhiteSpace(resource.Error?.Detail) ? "ACME Challenge 验证失败。" : $"ACME Challenge 验证失败：{resource.Error.Detail}");
			await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
		}
		throw new TimeoutException("ACME Challenge 验证超时。");
	}

	private async Task WaitDnsAsync(string name, string expected, CancellationToken cancellationToken)
	{
		var end = DateTimeOffset.UtcNow.AddSeconds(_options.DnsPropagationTimeoutSeconds);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		while (DateTimeOffset.UtcNow < end)
		{
			var found = false;
			foreach (var endpoint in _options.DnsQueryEndpoints.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}?name={Uri.EscapeDataString(name)}&type=TXT");
					request.Headers.Accept.ParseAdd("application/dns-json");
					using var response = await http.SendAsync(request, cancellationToken);
					if (!response.IsSuccessStatusCode)
						continue;
					found = true;
					using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
					if (json.RootElement.TryGetProperty("Answer", out var answers) && answers.EnumerateArray().Select(x => x.GetProperty("data").GetString()?.Trim('"')).Any(x => x == expected))
						return;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch (Exception ex) { logger.LogWarning("DNS 查询端点 {Endpoint} 失败：{Error}", endpoint, SafeError(ex)); }
			}
			if (!found)
				logger.LogWarning("所有 DNS 查询端点均不可用，等待后重试。域名：{Domain}", name);
			await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.DnsPollingIntervalSeconds)), cancellationToken);
		}
		throw new TimeoutException("DNS TXT 记录在配置的超时时间内未生效。");
	}

	private async Task<AcmeContext> CreateContextAsync(CancellationToken cancellationToken)
	{
		var server = string.Equals(_options.Environment, "Staging", StringComparison.OrdinalIgnoreCase) ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
		var path = Path.Combine(environment.ContentRootPath, _options.AccountKeyPath);
		System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		AcmeContext context;
		if (File.Exists(path))
			context = new AcmeContext(server, KeyFactory.FromPem(await File.ReadAllTextAsync(path, cancellationToken)));
		else
		{
			context = new AcmeContext(server);
			await context.NewAccount(_options.Email, true);
			await File.WriteAllTextAsync(path, context.AccountKey.ToPem(), cancellationToken);
		}
		return context;
	}

	private async Task SetStatusAsync(int id, CertificateStatus status, string message, CancellationToken cancellationToken)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var item = await db.Certificates.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("证书不存在。");
		item.Status = status;
		item.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(cancellationToken);
		await LogAsync(id, status, message, cancellationToken);
	}

	private async Task LogDnsTxtCleanupAsync(int id, CertificateStatus terminalStatus, List<string> cleanupMessages, CancellationToken cancellationToken)
	{
		foreach (var message in cleanupMessages)
			await LogAsync(id, terminalStatus, message, cancellationToken);
	}

	private async Task FailAsync(int id, string error, CancellationToken cancellationToken)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var item = await db.Certificates.FindAsync([id], cancellationToken);
		if (item is null)
			return;
		item.Status = CertificateStatus.Failed;
		item.ErrorMessage = error;
		item.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(cancellationToken);
		await LogAsync(id, CertificateStatus.Failed, error, cancellationToken);
	}

	private async Task LogAsync(int id, CertificateStatus status, string message, CancellationToken cancellationToken)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		db.CertificateLogs.Add(new CertificateLog { CertificateId = id, Status = status, Message = message });
		await db.SaveChangesAsync(cancellationToken);
		logger.LogInformation("证书 {CertificateId}：{Message}", id, message);
	}

	private static string StatusName(CertificateStatus? status) => status switch
	{
		CertificateStatus.Pending => "待申请",
		CertificateStatus.CreatingOrder => "创建订单中",
		CertificateStatus.CreatingChallenge => "创建验证中",
		CertificateStatus.WaitingDns => "等待 DNS 生效",
		CertificateStatus.Validating => "验证中",
		CertificateStatus.Issuing => "签发中",
		CertificateStatus.Issued => "已签发",
		CertificateStatus.Renewing => "续期中",
		CertificateStatus.Failed => "失败",
		_ => "不存在"
	};

	private static string FirstCertificate(string pem)
	{
		const string end = "-----END CERTIFICATE-----";
		var i = pem.IndexOf(end, StringComparison.Ordinal);
		return i < 0 ? pem : pem[..(i + end.Length)] + Environment.NewLine;
	}

	private static async Task<string?> ReadOrderFailureAsync(IOrderContext order)
	{
		try
		{
			var resource = await order.Resource();
			if (resource.Error is null)
				return $"ACME 订单状态：{resource.Status}";
			using var json = JsonDocument.Parse(resource.Error.ToString()!);
			var root = json.RootElement;
			var details = new List<string> { $"ACME 订单状态：{resource.Status}" };
			if (root.TryGetProperty("type", out var type))
				details.Add($"错误类型：{type.GetString()}");
			if (root.TryGetProperty("detail", out var detail))
				details.Add($"详情：{detail.GetString()}");
			if (root.TryGetProperty("subproblems", out var subproblems) && subproblems.ValueKind == JsonValueKind.Array)
			{
				var items = subproblems.EnumerateArray().Select(item => string.Join("，", new[] { item.TryGetProperty("type", out var itemType) ? itemType.GetString() : null, item.TryGetProperty("detail", out var itemDetail) ? itemDetail.GetString() : null }.Where(x => !string.IsNullOrWhiteSpace(x))));
				details.Add($"子问题：{string.Join("；", items.Where(x => !string.IsNullOrWhiteSpace(x)))}");
			}
			return string.Join("；", details);
		}
		catch (Exception ex) { return $"读取 ACME 订单错误详情失败：{ex.Message}"; }
	}

	private static string SafeError(Exception ex) => SafeError(ex.Message);

	private static string SafeError(string error) => error.Length > 1000 ? error[..1000] : error;
}
