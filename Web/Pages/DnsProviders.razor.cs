using CertManagement.Domain;

namespace CertManagement.Web.Pages;

public partial class DnsProviders
{
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