using CertManagement.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CertManagement.Web.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AdminCredentialService credentials) : ControllerBase
{
	[AllowAnonymous, HttpPost("login")]
	public async Task<IActionResult> Login([FromBody] LoginRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Username) || !await credentials.VerifyAsync(request.Username, request.Password))
			return Unauthorized(new
			{
				message = "用户名或密码错误。"
			});
		var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, request.Username)], CookieAuthenticationDefaults.AuthenticationScheme);
		await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
		return Ok(new
		{
			message = "登录成功。"
		});
	}

	[AllowAnonymous, HttpPost("login-form")]
	public async Task<IActionResult> LoginForm([FromForm] LoginRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Username) || !await credentials.VerifyAsync(request.Username, request.Password))
			return Redirect("/login?error=invalid");
		var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, request.Username)], CookieAuthenticationDefaults.AuthenticationScheme);
		await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
		return Redirect("/");
	}

	[Authorize, HttpGet("logout")]
	public async Task<IActionResult> Logout()
	{
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
		return Redirect("/login");
	}

	[Authorize, HttpPost("logout")]
	public async Task<IActionResult> LogoutPost()
	{
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
		return NoContent();
	}

	[Authorize, HttpPost("change-password")]
	public async Task<IActionResult> ChangePassword([FromForm] ChangePasswordRequest request)
	{
		var username = User.Identity?.Name;
		if (string.IsNullOrWhiteSpace(username))
			return Unauthorized();
		var result = await credentials.ChangePasswordAsync(username, request.OldPassword, request.NewPassword, request.ConfirmPassword);
		if (!result.Success)
			return Redirect($"/settings?passwordError={Uri.EscapeDataString(result.Error!)}");
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
		return Redirect("/login?changed=1");
	}

	public sealed record LoginRequest(string Username, string Password);
	public sealed record ChangePasswordRequest(string OldPassword, string NewPassword, string ConfirmPassword);
}