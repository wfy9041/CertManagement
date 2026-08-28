using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CertManagement.Application;

public sealed class CertificateRenewalService(IDbContextFactory<CertificateDbContext> dbFactory, IServiceScopeFactory scopeFactory, IOptions<RenewalOptions> options, ILogger<CertificateRenewalService> logger) : BackgroundService
{
	private readonly RenewalOptions _options = options.Value;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await ScanAsync(stoppingToken);
		using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.ScanIntervalMinutes)));
		while (await timer.WaitForNextTickAsync(stoppingToken))
			await ScanAsync(stoppingToken);
	}

	private async Task ScanAsync(CancellationToken cancellationToken)
	{
		try
		{
			await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
			var deadline = DateTime.UtcNow.AddDays(_options.RenewBeforeDays);
			var ids = await db.Certificates.Where(x => x.AutoRenew && x.Status == CertificateStatus.Issued && x.ExpiresAt != null && x.ExpiresAt <= deadline).Select(x => x.DomainId).ToListAsync(cancellationToken);
			foreach (var id in ids)
			{
				for (var attempt = 1; attempt <= Math.Max(1, _options.MaxAttempts); attempt++)
				{
					try
					{
						if (attempt > 1)
							await ReopenFailedRenewalAsync(id, cancellationToken);
						await using var scope = scopeFactory.CreateAsyncScope();
						await scope.ServiceProvider.GetRequiredService<CertificateService>().RequestAsync(id, true, cancellationToken);
						break;
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
					catch (Exception ex) { logger.LogWarning(ex, "自动续期失败，域名 ID {DomainId}，第 {Attempt} 次", id, attempt); if (attempt < _options.MaxAttempts) await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), cancellationToken); }
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex) { logger.LogError(ex, "自动续期扫描失败"); }
	}

	private async Task ReopenFailedRenewalAsync(int domainId, CancellationToken cancellationToken)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Certificates SET Status = {CertificateStatus.Issued.ToString()}, ErrorMessage = NULL, UpdatedAt = {DateTime.UtcNow} WHERE DomainId = {domainId} AND Status = {CertificateStatus.Failed.ToString()}", cancellationToken);
	}
}