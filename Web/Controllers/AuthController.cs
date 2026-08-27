using System.Security.Claims;
using CertManagement.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CertManagement.Web.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AdminCredentialService credentials) : ControllerBase
{
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || !credentials.Verify(request.Username, request.Password)) return Unauthorized(new { message = "用户名或密码错误。" });
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, request.Username)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)); return Ok(new { message = "登录成功。" });
    }
    [AllowAnonymous, HttpPost("login-form")]
    public async Task<IActionResult> LoginForm([FromForm] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || !credentials.Verify(request.Username, request.Password)) return Redirect("/login?error=invalid");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, request.Username)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)); return Redirect("/");
    }
    [Authorize, HttpGet("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Redirect("/login"); }
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> LogoutPost() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return NoContent(); }
    public sealed record LoginRequest(string Username, string Password);
}