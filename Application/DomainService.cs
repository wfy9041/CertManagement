using CertManagement.Domain;
using CertManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using DomainEntity = CertManagement.Domain.Domain;

namespace CertManagement.Application;

public sealed class DomainService(IDbContextFactory<CertificateDbContext> dbFactory)
{
	public async Task<List<DomainEntity>> ListAsync(CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.Domains.Include(x => x.DnsProviderAccount).Include(x => x.DeploymentTargets).OrderBy(x => x.PrimaryDomain).ToListAsync(cancellationToken);
	}

	public async Task<DomainEntity?> GetAsync(int id, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		return await db.Domains.Include(x => x.DnsProviderAccount).Include(x => x.DeploymentTargets).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
	}

	public async Task<DomainEntity> SaveAsync(DomainEntity input, CancellationToken cancellationToken = default)
	{
		var primary = NormalizeDomain(input.PrimaryDomain);
		var sans = NormalizeSans(input.Sans);
		var names = new[] { primary }.Concat(sans).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var hasWildcard = names.Any(x => x.StartsWith("*.", StringComparison.Ordinal));
		if (string.IsNullOrWhiteSpace(primary) || names.Any(x => x.Contains(' ')) || names.Any(x => x.Length > 253))
			throw new ValidationException("域名格式无效。");
		if (hasWildcard && input.ChallengeType != ChallengeType.Dns01)
			throw new ValidationException("通配符域名必须使用 DNS-01。");
		if (input.ChallengeType == ChallengeType.Dns01 && input.DnsProviderAccountId is null)
			throw new ValidationException("DNS-01 必须选择 DNS Provider。");

		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var entity = input.Id == 0
			? new DomainEntity()
			: await db.Domains.SingleOrDefaultAsync(x => x.Id == input.Id, cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
		if (input.Id == 0)
			db.Domains.Add(entity);
		entity.PrimaryDomain = primary;
		entity.Sans = string.Join("\n", sans);
		entity.ChallengeType = input.ChallengeType;
		entity.DnsProviderAccountId = input.ChallengeType == ChallengeType.Dns01 ? input.DnsProviderAccountId : null;
		entity.Enabled = input.Enabled;
		entity.AutoRenew = input.AutoRenew;
		entity.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var domain = await db.Domains.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
		domain.Enabled = enabled;
		domain.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(cancellationToken);
	}

	public async Task SetAutoRenewAsync(int id, bool autoRenew, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		var domain = await db.Domains.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
		domain.AutoRenew = autoRenew;
		await db.Certificates.Where(x => x.DomainId == id)
			.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AutoRenew, autoRenew), cancellationToken);
		domain.UpdatedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(cancellationToken);
	}

	public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
	{
		await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		var domain = await db.Domains.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("域名不存在。");
		if (await db.Certificates.AnyAsync(x => x.DomainId == id, cancellationToken))
			throw new InvalidOperationException("请先在证书管理中删除该域名下的所有证书。");

		var targets = db.DeploymentTargets.Where(x => x.DomainId == id);
		if (await db.CertificateDeployments.AnyAsync(x => targets.Any(t => t.Id == x.DeploymentTargetId), cancellationToken))
			throw new InvalidOperationException("该域名的部署目标仍被其他证书的部署记录引用，无法删除。");

		await targets.ExecuteDeleteAsync(cancellationToken);
		db.Domains.Remove(domain);
		await db.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	public static List<string> Names(DomainEntity domain) => new[] { domain.PrimaryDomain }
		.Concat(NormalizeSans(domain.Sans)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

	private static string NormalizeDomain(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();

	private static List<string> NormalizeSans(string value) => value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.Select(NormalizeDomain).Where(x => x.Length > 0).ToList();
}
