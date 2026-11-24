using CertManagement.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CertManagement.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/certificates")]
public sealed class CertificateController(CertificateService certificateService, CertificateFileService fileService) : ControllerBase
{
	[HttpPost("{id:int}/issue")]
	public async Task<IActionResult> Issue(int id, CancellationToken cancellationToken) => Ok(await certificateService.RequestAsync(id, false, cancellationToken));

	[HttpPost("{id:int}/renew")]
	public async Task<IActionResult> Renew(int id, CancellationToken cancellationToken) => Ok(await certificateService.RequestAsync(id, true, cancellationToken));

	[HttpDelete("{id:int}")]
	public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
	{
		try
		{
			await certificateService.DeleteAsync(id, cancellationToken);
			return NoContent();
		}
		catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
		catch (InvalidOperationException ex) { return Conflict(ex.Message); }
	}

	[HttpGet("{id:int}/download/{kind}")]
	public async Task<IActionResult> DownloadPem(int id, string kind, CancellationToken cancellationToken)
	{
		if (kind is not ("cert" or "chain" or "fullchain" or "privkey"))
			return BadRequest("不支持的证书文件类型。");
		var file = await fileService.ReadPemAsync(id, kind, cancellationToken);
		return File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
	}

	[HttpGet("{id:int}/download/bundle")]
	public async Task<IActionResult> DownloadBundle(int id, CancellationToken cancellationToken)
	{
		var file = await fileService.CreateBundleAsync(id, cancellationToken);
		return File(file.Content, "application/zip", file.FileName);
	}

	[HttpPost("{id:int}/download/pfx")]
	public async Task<IActionResult> DownloadPfx(int id, [FromBody] PfxRequest request, CancellationToken cancellationToken)
	{
		var file = await fileService.CreatePfxAsync(id, request.Password, cancellationToken);
		return File(file.Content, "application/x-pkcs12", file.FileName);
	}

	public sealed record PfxRequest(string Password);
}
