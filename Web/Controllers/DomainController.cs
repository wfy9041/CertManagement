using CertManagement.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DomainEntity = CertManagement.Domain.Domain;

namespace CertManagement.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/domains")]
public sealed class DomainController(DomainService service) : ControllerBase
{
	[HttpGet] public async Task<ActionResult<List<DomainEntity>>> List(CancellationToken cancellationToken) => await service.ListAsync(cancellationToken);

	[HttpPost] public async Task<ActionResult<DomainEntity>> Save([FromBody] DomainEntity model, CancellationToken cancellationToken) => Ok(await service.SaveAsync(model, cancellationToken));

	[HttpPatch("{id:int}/enabled")]
	public async Task<IActionResult> Enabled(int id, [FromBody] EnabledRequest request, CancellationToken cancellationToken)
	{
		await service.SetEnabledAsync(id, request.Enabled, cancellationToken);
		return NoContent();
	}

	public sealed record EnabledRequest(bool Enabled);

	[HttpDelete("{id:int}")]
	public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
	{
		try
		{
			await service.DeleteAsync(id, cancellationToken);
			return NoContent();
		}
		catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
		catch (InvalidOperationException ex) { return Conflict(ex.Message); }
	}
}
