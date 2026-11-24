using CertManagement.Domain;
using Microsoft.JSInterop;

namespace CertManagement.Web.Pages;

public partial class DnsProviders
{
	private int credentialSourceId;

	private void OnTypeChanged()
	{
		credentialSourceId = 0;
		apiKey = secret = string.Empty;
	}

	private async Task DeleteAsync(DnsProviderAccount item)
	{
		try
		{
			if (!await JS.InvokeAsync<bool>("confirm",
				$"确定删除 DNS 服务商“{item.Name}”及其在本系统保存的凭据？"))
				return;

			await Service.DeleteAsync(item.Id);
			message = "DNS 服务商已删除。";
			items = await Service.ListAsync();
		}
		catch (Exception ex) { message = ex.Message; }
	}

	private static string GetProviderLabel(DnsProviderType type) => type switch
	{
		DnsProviderType.Cloudflare => "Cloudflare",
		DnsProviderType.Aliyun => "阿里云 DNS（AliDNS）",
		DnsProviderType.AliyunEsa => "阿里云 ESA",
		_ => type.ToString()
	};

	private async Task ToggleEnabledAsync(DnsProviderAccount item)
	{
		try
		{
			await Service.SetEnabledAsync(item.Id, !item.Enabled);
			items = await Service.ListAsync();
		}
		catch (Exception ex)
		{
			message = ex.Message;
		}
	}
}
