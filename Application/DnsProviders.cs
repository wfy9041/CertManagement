using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CertManagement.Application;

public sealed record DnsTxtRecord(string Name, string Value, int Ttl = 600);

public interface IDnsProvider
{
	Task CreateTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default); Task DeleteTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default); Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

public interface IDnsProviderFactory
{
	IDnsProvider Create(DnsProviderAccount account);
}

public sealed class DnsProviderFactory(IHttpClientFactory httpClientFactory, IDataProtectionProvider protectionProvider) : IDnsProviderFactory
{
	public IDnsProvider Create(DnsProviderAccount account)
	{
		var protector = protectionProvider.CreateProtector("DnsProviderSecrets.v1");
		var apiKey = protector.Unprotect(account.ProtectedApiKeyOrToken);
		var secret = protector.Unprotect(account.ProtectedSecret);
		return account.Type switch
		{
			DnsProviderType.Cloudflare => new CloudflareDnsProvider(httpClientFactory.CreateClient(), apiKey, secret),
			DnsProviderType.Aliyun => new AliyunDnsProvider(httpClientFactory.CreateClient(), apiKey, secret),
			_ => throw new NotSupportedException($"不支持的 DNS Provider 类型：{account.Type}")
		};
	}
}

public sealed class DnsProviderService(IDbContextFactory<CertificateDbContext> dbFactory, IDataProtectionProvider protectionProvider, IDnsProviderFactory factory, ILogger<DnsProviderService> logger)
{
	private readonly IDataProtector protector = protectionProvider.CreateProtector("DnsProviderSecrets.v1");

	public async Task<List<DnsProviderAccount>> ListAsync(CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.DnsProviderAccounts.OrderBy(x => x.Name).ToListAsync(cancellationToken);
	}

	public async Task<DnsProviderAccount?> GetAsync(int id, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.DnsProviderAccounts.FindAsync([id], cancellationToken);
	}

	public async Task<DnsProviderAccount> SaveAsync(DnsProviderAccount input, string apiKey, string secret, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(input.Name))
			throw new ArgumentException("Provider 名称不能为空。", nameof(input));
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var entity = input.Id == 0 ? new DnsProviderAccount() : await db.DnsProviderAccounts.FindAsync([input.Id], cancellationToken) ?? throw new KeyNotFoundException("DNS Provider 不存在。");
		if (input.Id == 0)
			db.DnsProviderAccounts.Add(entity);
		entity.Name = input.Name.Trim();
		entity.Type = input.Type;
		entity.Enabled = input.Enabled;
		entity.UpdatedAt = DateTime.UtcNow;
		if (!string.IsNullOrWhiteSpace(apiKey))
			entity.ProtectedApiKeyOrToken = protector.Protect(apiKey.Trim());
		if (!string.IsNullOrWhiteSpace(secret))
			entity.ProtectedSecret = protector.Protect(secret);
		await db.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task<bool> TestAsync(int id, CancellationToken cancellationToken = default)
	{
		var account = await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException("DNS Provider 不存在。");
		try
		{
			return await factory.Create(account).TestConnectionAsync(cancellationToken);
		}
		catch (Exception ex) { logger.LogWarning("DNS Provider {ProviderId} 连接测试失败：{Error}", id, SafeError(ex)); return false; }
	}

	public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var account = await db.DnsProviderAccounts.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("DNS Provider 不存在。");
		account.Enabled = enabled;
		account.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(cancellationToken);
	}

	private static string SafeError(Exception ex) => ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
}

public abstract class DnsProviderBase(HttpClient client, string apiKey, string secret) : IDnsProvider
{
	protected readonly HttpClient Client = client; protected readonly string ApiKey = apiKey; protected readonly string Secret = secret;

	public abstract Task CreateTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default); public abstract Task DeleteTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default); public abstract Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

	protected static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode)
			return;
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		throw new HttpRequestException($"DNS API 返回 {(int)response.StatusCode}：{body[..Math.Min(300, body.Length)]}");
	}
}

public sealed class CloudflareDnsProvider(HttpClient client, string apiToken, string emailOrApiKey) : DnsProviderBase(client, apiToken, emailOrApiKey)
{
	private const string BaseUrl = "https://api.cloudflare.com/client/v4";

	public override async Task CreateTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default)
	{
		var zone = await FindZoneAsync(record.Name, cancellationToken);
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/zones/{zone}/dns_records");
		ApplyAuth(request);
		request.Content = JsonContent.Create(new
		{
			type = "TXT",
			name = record.Name,
			content = record.Value,
			ttl = record.Ttl,
			proxied = false
		});
		await EnsureSuccessAsync(await Client.SendAsync(request, cancellationToken), cancellationToken);
	}

	public override async Task DeleteTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default)
	{
		var zone = await FindZoneAsync(record.Name, cancellationToken);
		foreach (var id in await GetRecordIdsAsync(zone, record, cancellationToken))
		{
			using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/zones/{zone}/dns_records/{id}");
			ApplyAuth(request);
			await EnsureSuccessAsync(await Client.SendAsync(request, cancellationToken), cancellationToken);
		}
	}

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/user/tokens/verify");
		ApplyAuth(request);
		return (await Client.SendAsync(request, cancellationToken)).IsSuccessStatusCode;
	}

	private void ApplyAuth(HttpRequestMessage request)
	{
		if (string.IsNullOrWhiteSpace(Secret))
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
		else
		{
			request.Headers.Add("X-Auth-Email", Secret);
			request.Headers.Add("X-Auth-Key", ApiKey);
		}
	}

	private async Task<string> FindZoneAsync(string name, CancellationToken cancellationToken)
	{
		var clean = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
		var labels = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < labels.Length - 1; i++)
		{
			var candidate = string.Join('.', labels[i..]);
			using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/zones?name={Uri.EscapeDataString(candidate)}&status=active");
			ApplyAuth(request);
			using var response = await Client.SendAsync(request, cancellationToken);
			await EnsureSuccessAsync(response, cancellationToken);
			using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
			var result = json.RootElement.GetProperty("result");
			if (result.GetArrayLength() > 0)
				return result[0].GetProperty("id").GetString()!;
		}
		throw new InvalidOperationException("Cloudflare 未找到对应 DNS Zone。");
	}

	private async Task<List<string>> GetRecordIdsAsync(string zone, DnsTxtRecord record, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/zones/{zone}/dns_records?type=TXT&name={Uri.EscapeDataString(record.Name)}");
		ApplyAuth(request);
		using var response = await Client.SendAsync(request, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
		return json.RootElement.GetProperty("result").EnumerateArray().Where(x => x.GetProperty("content").GetString() == record.Value).Select(x => x.GetProperty("id").GetString()!).ToList();
	}
}

public sealed class AliyunDnsProvider(HttpClient client, string accessKeyId, string accessKeySecret) : DnsProviderBase(client, accessKeyId, accessKeySecret)
{
	private const string Endpoint = "https://alidns.aliyuncs.com/";

	public override async Task CreateTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default)
	{
		var domain = await FindDomainAsync(record.Name, cancellationToken);
		var rr = RelativeRecordName(record.Name, domain);
		var query = await SendAsync("AddDomainRecord", new Dictionary<string, string> { ["DomainName"] = domain, ["RR"] = rr, ["Type"] = "TXT", ["Value"] = record.Value, ["TTL"] = record.Ttl.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
		EnsureAliyunSuccess(query);
	}

	public override async Task DeleteTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default)
	{
		var domain = await FindDomainAsync(record.Name, cancellationToken);
		var rr = RelativeRecordName(record.Name, domain);
		var response = await SendAsync("DescribeDomainRecords", new Dictionary<string, string> { ["DomainName"] = domain, ["RRKeyWord"] = rr, ["TypeKeyWord"] = "TXT", ["PageSize"] = "100" }, cancellationToken);
		EnsureAliyunSuccess(response);
		if (!response.RootElement.TryGetProperty("DomainRecords", out var records))
			return;
		foreach (var item in records.GetProperty("Record").EnumerateArray().Where(x => x.GetProperty("Value").GetString() == record.Value))
		{
			var id = item.GetProperty("RecordId").GetString();
			if (string.IsNullOrWhiteSpace(id))
				continue;
			var deleted = await SendAsync("DeleteDomainRecord", new Dictionary<string, string> { ["RecordId"] = id }, cancellationToken);
			EnsureAliyunSuccess(deleted);
		}
	}

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		var response = await SendAsync("DescribeDomains", new Dictionary<string, string> { ["PageSize"] = "1" }, cancellationToken);
		return IsAliyunSuccess(response);
	}

	private async Task<string> FindDomainAsync(string name, CancellationToken cancellationToken)
	{
		var clean = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
		var response = await SendAsync("DescribeDomains", new Dictionary<string, string> { ["PageSize"] = "100" }, cancellationToken);
		EnsureAliyunSuccess(response);
		if (!response.RootElement.TryGetProperty("Domains", out var domains))
			throw new InvalidOperationException("阿里云未返回 DNS 域名列表。");
		var matches = domains.GetProperty("Domain").EnumerateArray().Select(x => x.GetProperty("DomainName").GetString()!).Where(x => clean.Equals(x, StringComparison.OrdinalIgnoreCase) || clean.EndsWith("." + x, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Length).ToList();
		return matches.FirstOrDefault() ?? throw new InvalidOperationException("阿里云未找到对应 DNS 域名。");
	}

	private async Task<JsonDocument> SendAsync(string action, IReadOnlyDictionary<string, string> extra, CancellationToken cancellationToken)
	{
		var query = new Dictionary<string, string>(extra, StringComparer.Ordinal) { ["AccessKeyId"] = ApiKey, ["Action"] = action, ["Format"] = "JSON", ["SignatureMethod"] = "HMAC-SHA1", ["SignatureNonce"] = Guid.NewGuid().ToString("N"), ["SignatureVersion"] = "1.0", ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["Version"] = "2015-01-09" };
		var canonical = string.Join('&', query.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Encode(x.Key) + "=" + Encode(x.Value)));
		var signText = "GET&%2F&" + Encode(canonical);
		using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(Secret + "&"));
		query["Signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signText)));
		using var response = await Client.GetAsync(Endpoint + "?" + string.Join('&', query.Select(x => Encode(x.Key) + "=" + Encode(x.Value))), cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
	}

	private static string RelativeRecordName(string fullName, string domain)
	{
		var clean = fullName.TrimEnd('.');
		var suffix = "." + domain.TrimEnd('.');
		if (!clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("TXT 记录不属于目标域名。");
		var rr = clean[..^(suffix.Length)];
		return string.IsNullOrWhiteSpace(rr) ? "@" : rr.TrimEnd('.');
	}

	private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

	private static bool IsAliyunSuccess(JsonDocument response) => !response.RootElement.TryGetProperty("Code", out var code) || string.IsNullOrWhiteSpace(code.GetString());

	private static void EnsureAliyunSuccess(JsonDocument response)
	{
		if (response.RootElement.TryGetProperty("Code", out var code) && code.GetString() is { Length: > 0 } value)
			throw new InvalidOperationException($"阿里云 DNS API 返回错误：{value}");
	}
}