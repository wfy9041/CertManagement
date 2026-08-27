using CertManagement.Application;
using CertManagement.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Config;
using NLog.Web;

var bootstrapConfig = new XmlLoggingConfiguration("nlog.config");
LogManager.Setup().LoadConfiguration(bootstrapConfig);
var bootstrapLogger = LogManager.GetCurrentClassLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);
    Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
    Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "certificates"));
    Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "logs"));
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();
    builder.Services.Configure<AcmeOptions>(builder.Configuration.GetSection(AcmeOptions.SectionName));
    builder.Services.Configure<RenewalOptions>(builder.Configuration.GetSection(RenewalOptions.SectionName));
    builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
    builder.Services.AddDataProtection();
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.LoginPath = "/login"; options.AccessDeniedPath = "/login"; options.SlidingExpiration = true; options.ExpireTimeSpan = TimeSpan.FromHours(8); });
    builder.Services.AddAuthorization();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddDbContextFactory<CertificateDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
    builder.Services.AddHttpClient();
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddControllers();
    builder.Services.AddScoped<AdminCredentialService>();
    builder.Services.AddScoped<DomainService>();
    builder.Services.AddScoped<DnsProviderService>();
    builder.Services.AddScoped<CertificateService>();
    builder.Services.AddScoped<CertificateFileService>();
    builder.Services.AddSingleton<ChallengeStore>();
    builder.Services.AddSingleton<IDnsProviderFactory, DnsProviderFactory>();
    builder.Services.AddHostedService<CertificateRenewalService>();
    var app = builder.Build();
    using (var scope = app.Services.CreateScope()) { var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CertificateDbContext>>(); await using var db = await factory.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
    app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery(); app.MapControllers();
    app.MapGet("/.well-known/acme-challenge/{token}", (string token, ChallengeStore store) => store.TryGet(token, out var value) ? Results.Text(value, "text/plain") : Results.NotFound());
    app.MapRazorComponents<CertManagement.Web.Components.App>().AddInteractiveServerRenderMode();
    await app.RunAsync();
}
catch (Exception ex)
{
    bootstrapLogger.Error(ex, "应用启动失败");
    throw;
}
finally { LogManager.Shutdown(); }
