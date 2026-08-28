using CertManagement.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CertManagement.Web.Controllers;

using WafDeploymentTarget = CertManagement.Domain.DeploymentTarget;

[ApiController, Authorize, Route("api/waf-deployments")]
public sealed class WafDeploymentController(WafDeploymentService service) : ControllerBase
{
	[HttpGet("targets")] public async Task<ActionResult<List<WafDeploymentTarget>>> Targets(CancellationToken ct) => await service.ListTargetsAsync(ct);

	[HttpPost("targets")] public async Task<ActionResult<WafDeploymentTarget>> Save([FromBody] SaveRequest request, CancellationToken ct) => Ok(await service.SaveTargetAsync(request.Model, request.Secret, ct));

	[HttpPost("targets/{id:int}/test")] public async Task<ActionResult<bool>> Test(int id, CancellationToken ct) => await service.TestAsync(id, ct);

	[HttpPost("certificates/{id:int}/deploy")]
	public async Task<IActionResult> Deploy(int id, CancellationToken ct)
	{
		await service.DeployAsync(id, ct: ct);
		return Accepted();
	}

	[HttpPost("domains/{id:int}/target")]
	public async Task<IActionResult> SetDomainTarget(int id, [FromBody] DomainTargetRequest request, CancellationToken ct)
	{
		await service.SetDomainTargetAsync(id, request.TargetId, request.WafDomain, ct);
		return NoContent();
	}

	[HttpPost("targets/{id:int}/validate-domain")]
	public async Task<IActionResult> ValidateDomain(int id, [FromBody] ValidateDomainRequest request, CancellationToken ct)
	{
		await service.ValidateDomainAsync(id, request.WafDomain, ct);
		return Ok();
	}

	[HttpPost("targets/{id:int}/domains")]
	public async Task<IActionResult> SetTargetDomains(int id, [FromBody] SetTargetDomainsRequest request, CancellationToken ct)
	{
		await service.SetTargetDomainsAsync(id, request.DomainIds, ct);
		return NoContent();
	}

	public sealed record SaveRequest(WafDeploymentTarget Model, string Secret);
	public sealed record DomainTargetRequest(int? TargetId, string? WafDomain);
	public sealed record ValidateDomainRequest(string WafDomain);
	public sealed record SetTargetDomainsRequest(IReadOnlyCollection<int> DomainIds);
}