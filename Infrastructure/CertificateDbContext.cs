using CertManagement.Domain;
using Microsoft.EntityFrameworkCore;
using DomainEntity = CertManagement.Domain.Domain;

namespace CertManagement.Infrastructure;

public sealed class CertificateDbContext(DbContextOptions<CertificateDbContext> options) : DbContext(options)
{
    public DbSet<AcmeAccount> AcmeAccounts => Set<AcmeAccount>();
    public DbSet<DnsProviderAccount> DnsProviderAccounts => Set<DnsProviderAccount>();
    public DbSet<DomainEntity> Domains => Set<DomainEntity>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<CertificateOrder> CertificateOrders => Set<CertificateOrder>();
    public DbSet<CertificateLog> CertificateLogs => Set<CertificateLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AcmeAccount>().HasIndex(x => new { x.Environment, x.Email }).IsUnique();
        modelBuilder.Entity<DnsProviderAccount>().Property(x => x.Type).HasConversion<string>();
        modelBuilder.Entity<DomainEntity>().Property(x => x.ChallengeType).HasConversion<string>();
        modelBuilder.Entity<DomainEntity>().HasIndex(x => x.PrimaryDomain);
        modelBuilder.Entity<DomainEntity>().HasOne(x => x.DnsProviderAccount).WithMany(x => x.Domains)
            .HasForeignKey(x => x.DnsProviderAccountId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Certificate>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<Certificate>().Property(x => x.RowVersion).IsConcurrencyToken();
        modelBuilder.Entity<Certificate>().HasOne(x => x.Domain).WithMany(x => x.Certificates)
            .HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CertificateOrder>().HasOne(x => x.Certificate).WithMany(x => x.Orders)
            .HasForeignKey(x => x.CertificateId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CertificateLog>().HasOne(x => x.Certificate).WithMany(x => x.Logs)
            .HasForeignKey(x => x.CertificateId).OnDelete(DeleteBehavior.Cascade);
    }
}
