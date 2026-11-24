using CertManagement.Domain;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CertManagement.Application;

internal sealed class EsaDeploymentClient(HttpClient client, string accessKeyId, string secret, string endpoint)
{
    internal const string DefaultEndpoint = "esa.cn-hangzhou.aliyuncs.com";

    internal static string NormalizeEndpoint(string? endpoint)
    {
        var host = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        if (host != DefaultEndpoint && host != "esa.ap-southeast-1.aliyuncs.com")
            throw new ArgumentException("ESA Endpoint 仅支持杭州或新加坡的官方接入点（不带协议和路径）。");
        return host;
    }

    internal async Task ValidateSiteAsync(DeploymentTarget target, string domain, CancellationToken ct)
    {
        using var json = await SendAsync("GetSite", new Dictionary<string, string> { ["SiteId"] = target.InstanceId }, null, ct);
        var site = json.RootElement.GetProperty("SiteModel");
        var siteName = site.GetProperty("SiteName").GetString()?.TrimEnd('.');
        var name = domain.Trim().TrimEnd('.');
        if (name.StartsWith("*.", StringComparison.Ordinal)) name = name[2..];
        if (string.IsNullOrEmpty(siteName) || !(name.Equals(siteName, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + siteName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("目标域名不属于指定 ESA 站点，请检查 SiteId 和目标域名。");
        if (site.GetProperty("Status").GetString() != "active")
            throw new InvalidOperationException("ESA 站点尚未激活或已停用，请先检查站点状态。");
    }

    internal Task<JsonDocument> DeployAsync(DeploymentTarget target, string certificate, string privateKey, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["SiteId"] = target.InstanceId,
            ["Name"] = $"certmanagement-{target.Id}",
            ["Type"] = "upload",
            ["Certificate"] = certificate,
            ["PrivateKey"] = privateKey
        };
        if (!string.IsNullOrWhiteSpace(target.EsaCertificateId)) body["Id"] = target.EsaCertificateId;
        return SendAsync("SetCertificate", new Dictionary<string, string>(), body, ct);
    }

    private async Task<JsonDocument> SendAsync(string action, IReadOnlyDictionary<string, string> query, Dictionary<string, string>? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("请配置 ESA AccessKeyId 和 AccessKeySecret。");
        var method = action switch
        {
            "GetSite" => HttpMethod.Get,
            "SetCertificate" => HttpMethod.Post,
            _ => throw new NotSupportedException($"不支持的 ESA 部署接口：{action}")
        };
        var host = NormalizeEndpoint(endpoint);
        var canonicalQuery = string.Join('&', query.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Encode(x.Key) + "=" + Encode(x.Value)));
        using var content = body is null ? null : new FormUrlEncodedContent(body);
        var payload = content is null ? Array.Empty<byte>() : await content.ReadAsByteArrayAsync(ct);
        var payloadHash = Hex(SHA256.HashData(payload));
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-acs-action"] = action,
            ["x-acs-content-sha256"] = payloadHash,
            ["x-acs-date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["x-acs-signature-nonce"] = Guid.NewGuid().ToString("N"),
            ["x-acs-version"] = "2024-09-10"
        };
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(x => x.Key + ":" + x.Value + "\n"));
        var canonicalRequest = method.Method + "\n/\n" + canonicalQuery + "\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + payloadHash;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes("ACS3-HMAC-SHA256\n" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))))));
        using var request = new HttpRequestMessage(method, "https://" + host + "/" + (canonicalQuery.Length == 0 ? "" : "?" + canonicalQuery)) { Content = content };
        foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
        request.Headers.TryAddWithoutValidation("Authorization", $"ACS3-HMAC-SHA256 Credential={accessKeyId},SignedHeaders={signedHeaders},Signature={signature}");
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new InvalidOperationException($"ESA {action} 网络请求失败；如为上传操作，请先核对云端结果再重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException($"ESA {action} 请求超时；如为上传操作，请先核对云端结果再重试。"); }
        using (response)
        {
            JsonDocument json;
            try { json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { throw new InvalidOperationException($"ESA {action} 返回无效响应（HTTP {(int)response.StatusCode}）。"); }
            if (!response.IsSuccessStatusCode || (json.RootElement.TryGetProperty("Code", out var code) && !string.IsNullOrWhiteSpace(code.GetString())))
            {
                // Response bodies may echo the private key or signature input; never expose them.
                var errorCode = json.RootElement.TryGetProperty("Code", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                var safeCode = errorCode is { Length: > 0 and <= 100 } && errorCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? "，错误码 " + errorCode : "";
                var hint = errorCode == "UnsupportedHTTPMethod"
                    ? "请检查客户端 HTTP 请求方法配置，此错误不能通过增加 RAM 权限解决。"
                    : "请检查 ESA 权限及站点配置。";
                json.Dispose();
                throw new InvalidOperationException($"ESA {action} 失败（HTTP {(int)response.StatusCode}{safeCode}），{hint}");
            }
            return json;
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
