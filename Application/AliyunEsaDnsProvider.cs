using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CertManagement.Application;

public sealed class AliyunEsaDnsProvider(HttpClient client, string accessKeyId, string accessKeySecret, int providerId, ILogger<AliyunEsaDnsProvider> logger)
    : DnsProviderBase(client, accessKeyId, accessKeySecret)
{
    private const string Endpoint = "https://esa.cn-hangzhou.aliyuncs.com/";
    private const int PageSize = 100;

    public override async Task CreateTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default)
    {
        var siteId = await FindSiteAsync(record.Name, cancellationToken);
        using var response = await SendAsync("CreateRecord", new Dictionary<string, string>
        {
            ["SiteId"] = siteId,
            ["RecordName"] = record.Name.TrimEnd('.'),
            ["Type"] = "TXT",
            ["Ttl"] = record.Ttl.ToString(CultureInfo.InvariantCulture),
            ["Proxied"] = "false",
            ["Data"] = JsonSerializer.Serialize(new { Value = record.Value })
        }, cancellationToken);
        logger.LogInformation("阿里云 ESA 已创建 TXT 记录。DNS 服务商 ID={ProviderId}，SiteId={SiteId}，记录名={RecordName}，RequestId={RequestId}",
            providerId, siteId, record.Name.TrimEnd('.'), ReadDiagnosticId(response.RootElement, "RequestId") ?? "未返回");
    }

    public override async Task DeleteTxtRecordAsync(DnsTxtRecord record, CancellationToken cancellationToken = default)
    {
        var siteId = await FindSiteAsync(record.Name, cancellationToken);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Collect every matching page before deleting, so deletion cannot shift later pages.
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync("ListRecords", new Dictionary<string, string>
            {
                ["SiteId"] = siteId,
                ["RecordName"] = record.Name.TrimEnd('.'),
                ["RecordMatchType"] = "exact",
                ["Type"] = "TXT",
                ["PageNumber"] = page.ToString(CultureInfo.InvariantCulture),
                ["PageSize"] = PageSize.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
            var root = response.RootElement;
            var records = root.GetProperty("Records");
            foreach (var item in records.EnumerateArray())
            {
                if (string.Equals(item.GetProperty("RecordName").GetString()?.TrimEnd('.'), record.Name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                    && item.GetProperty("RecordType").GetString() == "TXT"
                    && item.GetProperty("Data").GetProperty("Value").GetString() == record.Value)
                {
                    ids.Add(ReadId(item, "RecordId"));
                }
            }
            if ((long)page * PageSize >= root.GetProperty("TotalCount").GetInt64() || records.GetArrayLength() == 0)
                break;
        }
        foreach (var id in ids)
        {
            using var response = await SendAsync("DeleteRecord", new Dictionary<string, string> { ["RecordId"] = id }, cancellationToken);
        }
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("ListSites", new Dictionary<string, string> { ["PageNumber"] = "1", ["PageSize"] = "1" }, cancellationToken);
        return response.RootElement.GetProperty("Sites").ValueKind == JsonValueKind.Array;
    }

    private async Task<string> FindSiteAsync(string recordName, CancellationToken cancellationToken)
    {
        var name = recordName.TrimEnd('.');
        JsonElement? bestMatch = null;
        var bestLength = 0;
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync("ListSites", new Dictionary<string, string>
            {
                ["PageNumber"] = page.ToString(CultureInfo.InvariantCulture),
                ["PageSize"] = PageSize.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
            var root = response.RootElement;
            var sites = root.GetProperty("Sites");
            foreach (var site in sites.EnumerateArray())
            {
                var siteName = site.GetProperty("SiteName").GetString()?.TrimEnd('.');
                if (!string.IsNullOrEmpty(siteName) && siteName.Length > bestLength
                    && (name.Equals(siteName, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + siteName, StringComparison.OrdinalIgnoreCase)))
                {
                    bestMatch = site.Clone();
                    bestLength = siteName.Length;
                }
            }
            if ((long)page * PageSize >= root.GetProperty("TotalCount").GetInt64() || sites.GetArrayLength() == 0)
                break;
        }
        if (bestMatch is not { } match)
            throw new InvalidOperationException($"阿里云 ESA 未找到对应站点。DNS 服务商 ID={providerId}，记录名={name}。请确认域名已接入且凭据具有站点查询权限。");
        var siteId = ReadId(match, "SiteId");
        var accessType = match.GetProperty("AccessType").GetString();
        var status = match.GetProperty("Status").GetString();
        logger.LogInformation("阿里云 ESA 站点匹配。DNS 服务商 ID={ProviderId}，记录名={RecordName}，站点={SiteName}，SiteId={SiteId}，接入方式={AccessType}，站点状态={SiteStatus}",
            providerId, name, match.GetProperty("SiteName").GetString(), siteId, accessType, status);
        if (accessType != "NS")
            throw new InvalidOperationException("阿里云 ESA DNS-01 需要 NS 托管站点；CNAME 接入域名请使用其权威 DNS 服务商完成验证。");
        if (status != "active")
            throw new InvalidOperationException("阿里云 ESA 站点尚未激活，请先完成 NS 托管配置。");
        return siteId;
    }

    private async Task<JsonDocument> SendAsync(string action, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Secret))
            throw new InvalidOperationException("阿里云 ESA 需要配置 AccessKeyId 和 AccessKeySecret。");
        var diagnostic = $"DNS 服务商 ID={providerId}";
        if (parameters.TryGetValue("SiteId", out var siteId))
            diagnostic += $"，SiteId={siteId}";
        if (parameters.TryGetValue("RecordName", out var recordName))
            diagnostic += $"，记录名={recordName}";
        if (parameters.TryGetValue("RecordId", out var recordId))
            diagnostic += $"，RecordId={recordId}";
        // ESA RPC actions do not all accept the same HTTP method.
        var method = action switch
        {
            "ListSites" or "ListRecords" => HttpMethod.Get,
            "CreateRecord" or "DeleteRecord" => HttpMethod.Post,
            _ => throw new NotSupportedException($"不支持的 ESA DNS 接口：{action}")
        };
        var canonicalQuery = string.Join('&', parameters.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Encode(x.Key) + "=" + Encode(x.Value)));
        var payloadHash = Sha256Hex(string.Empty);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = new Uri(Endpoint).Host,
            ["x-acs-action"] = action,
            ["x-acs-content-sha256"] = payloadHash,
            ["x-acs-date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["x-acs-signature-nonce"] = Guid.NewGuid().ToString("N"),
            ["x-acs-version"] = "2024-09-10"
        };
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(x => x.Key + ":" + x.Value + "\n"));
        var canonicalRequest = method.Method + "\n/\n" + canonicalQuery + "\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + payloadHash;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("ACS3-HMAC-SHA256\n" + Sha256Hex(canonicalRequest)))).ToLowerInvariant();
        using var request = new HttpRequestMessage(method, Endpoint + "?" + canonicalQuery);
        foreach (var header in headers)
            request.Headers.Add(header.Key, header.Value);
        request.Headers.TryAddWithoutValidation("Authorization", $"ACS3-HMAC-SHA256 Credential={ApiKey},SignedHeaders={signedHeaders},Signature={signature}");
        // Credentials stay in the authentication header; transport errors never echo the request.
        HttpResponseMessage response;
        try { response = await Client.SendAsync(request, cancellationToken); }
        catch (HttpRequestException) { throw new HttpRequestException($"阿里云 ESA {action} 请求失败（{diagnostic}），请检查网络连接。"); }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonDocument json;
            try { json = JsonDocument.Parse(body); }
            catch (JsonException) { throw new InvalidOperationException($"阿里云 ESA {action} 返回无效响应（HTTP {(int)response.StatusCode}，{diagnostic}）。"); }
            var root = json.RootElement;
            if (!response.IsSuccessStatusCode || (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Code", out var code) && code.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(code.GetString())))
            {
                // Do not propagate the API body: it may echo request parameters.
                var errorCode = ReadDiagnosticId(json.RootElement, "Code") ?? "未返回";
                var requestId = ReadDiagnosticId(json.RootElement, "RequestId") ?? "未返回";
                var hint = errorCode == "UnsupportedHTTPMethod"
                    ? "请检查客户端 HTTP 请求方法配置，此错误不能通过增加 RAM 权限解决。"
                    : action == "CreateRecord" && (int)response.StatusCode == 403
                        ? "请核对该 DNS 服务商使用的 RAM 身份是否对这个 SiteId 有 esa:CreateRecord 权限。"
                        : "请检查 ESA 权限及站点配置。";
                json.Dispose();
                throw new InvalidOperationException($"阿里云 ESA {action} 失败（HTTP {(int)response.StatusCode}，错误码={errorCode}，RequestId={requestId}，{diagnostic}）。{hint}");
            }
            return json;
        }
    }

    private static string? ReadDiagnosticId(JsonElement item, string property)
    {
        var value = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        return value is { Length: > 0 and <= 100 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? value : null;
    }

    private static string ReadId(JsonElement item, string property)
    {
        var value = item.GetProperty(property);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("阿里云 ESA 返回无效的站点或记录 ID。");
    }

    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

    private static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
