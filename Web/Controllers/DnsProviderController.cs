using CertManagement.Application;
using CertManagement.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CertManagement.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/dns-providers")]
public sealed class DnsProviderController(DnsProviderService service) : ControllerBase
{
	[HttpGet] public async Task<ActionResult<List<DnsProviderAccount>>> List(CancellationToken cancellationToken) => await service.ListAsync(cancellationToken);

	[HttpPost] public async Task<ActionResult<DnsProviderAccount>> Save([FromBody] SaveRequest request, CancellationToken cancellationToken) => Ok(await service.SaveAsync(request.Model, request.ApiKey, request.Secret, cancellationToken));

	[HttpPost("{id:int}/test")] public async Task<ActionResult<bool>> Test(int id, CancellationToken cancellationToken) => await service.TestAsync(id, cancellationToken);

	public sealed record SaveRequest(DnsProviderAccount Model, string ApiKey, string Secret);
}