using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertManagement.Infrastructure.Migrations;

[DbContext(typeof(CertManagement.Infrastructure.CertificateDbContext))]
[Migration("202608270001_InitialCreate")]
public partial class InitialCreate : Migration
{
	protected override void Up(MigrationBuilder m)
	{
		m.CreateTable(name: "AcmeAccounts", columns: t => new { Id = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), Environment = t.Column<string>(nullable: false), Email = t.Column<string>(nullable: false), AccountUrl = t.Column<string>(nullable: false), ProtectedPrivateKey = t.Column<string>(nullable: false), CreatedAt = t.Column<DateTime>(nullable: false) }, constraints: t => t.PrimaryKey("PK_AcmeAccounts", x => x.Id));
		m.CreateTable(name: "DnsProviderAccounts", columns: t => new { Id = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), Name = t.Column<string>(nullable: false), Type = t.Column<string>(nullable: false), ProtectedApiKeyOrToken = t.Column<string>(nullable: false), ProtectedSecret = t.Column<string>(nullable: false), Enabled = t.Column<bool>(nullable: false), CreatedAt = t.Column<DateTime>(nullable: false), UpdatedAt = t.Column<DateTime>(nullable: false) }, constraints: t => t.PrimaryKey("PK_DnsProviderAccounts", x => x.Id));
		m.CreateTable(name: "Domains", columns: t => new { Id = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), PrimaryDomain = t.Column<string>(nullable: false), Sans = t.Column<string>(nullable: false), ChallengeType = t.Column<string>(nullable: false), DnsProviderAccountId = t.Column<int>(nullable: true), Enabled = t.Column<bool>(nullable: false), AutoRenew = t.Column<bool>(nullable: false), CreatedAt = t.Column<DateTime>(nullable: false), UpdatedAt = t.Column<DateTime>(nullable: false) }, constraints: t => { t.PrimaryKey("PK_Domains", x => x.Id); t.ForeignKey("FK_Domains_DnsProviderAccounts_DnsProviderAccountId", x => x.DnsProviderAccountId, "DnsProviderAccounts", "Id", onDelete: ReferentialAction.SetNull); });
		m.CreateTable(name: "Certificates", columns: t => new { Id = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), DomainId = t.Column<int>(nullable: false), PrimaryDomain = t.Column<string>(nullable: false), Sans = t.Column<string>(nullable: false), IssuedAt = t.Column<DateTime>(nullable: true), ExpiresAt = t.Column<DateTime>(nullable: true), LastRenewAt = t.Column<DateTime>(nullable: true), NextRenewAt = t.Column<DateTime>(nullable: true), Status = t.Column<string>(nullable: false), AutoRenew = t.Column<bool>(nullable: false), CertificatePath = t.Column<string>(nullable: true), PrivateKeyPath = t.Column<string>(nullable: true), FullChainPath = t.Column<string>(nullable: true), ChainPath = t.Column<string>(nullable: true), ErrorMessage = t.Column<string>(nullable: true), UpdatedAt = t.Column<DateTime>(nullable: false), RowVersion = t.Column<byte[]>(nullable: false), RenewalLockId = t.Column<string>(nullable: true), RenewalLockExpiresAt = t.Column<DateTime>(nullable: true) }, constraints: t => { t.PrimaryKey("PK_Certificates", x => x.Id); t.ForeignKey("FK_Certificates_Domains_DomainId", x => x.DomainId, "Domains", "Id", onDelete: ReferentialAction.Cascade); });
		m.CreateTable(name: "CertificateOrders", columns: t => new { Id = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), CertificateId = t.Column<int>(nullable: false), OrderUrl = t.Column<string>(nullable: false), AuthorizationData = t.Column<string>(nullable: true), ErrorMessage = t.Column<string>(nullable: true), CreatedAt = t.Column<DateTime>(nullable: false), CompletedAt = t.Column<DateTime>(nullable: true) }, constraints: t => { t.PrimaryKey("PK_CertificateOrders", x => x.Id); t.ForeignKey("FK_CertificateOrders_Certificates_CertificateId", x => x.CertificateId, "Certificates", "Id", onDelete: ReferentialAction.Cascade); });
		m.CreateTable(name: "CertificateLogs", columns: t => new { Id = t.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true), CertificateId = t.Column<int>(nullable: false), Status = t.Column<string>(nullable: false), Message = t.Column<string>(nullable: false), CreatedAt = t.Column<DateTime>(nullable: false) }, constraints: t => { t.PrimaryKey("PK_CertificateLogs", x => x.Id); t.ForeignKey("FK_CertificateLogs_Certificates_CertificateId", x => x.CertificateId, "Certificates", "Id", onDelete: ReferentialAction.Cascade); });
		m.CreateIndex(name: "IX_AcmeAccounts_Environment_Email", table: "AcmeAccounts", columns: new[] { "Environment", "Email" }, unique: true);
		m.CreateIndex(name: "IX_Certificates_DomainId", table: "Certificates", column: "DomainId");
		m.CreateIndex(name: "IX_CertificateLogs_CertificateId", table: "CertificateLogs", column: "CertificateId");
		m.CreateIndex(name: "IX_CertificateOrders_CertificateId", table: "CertificateOrders", column: "CertificateId");
		m.CreateIndex(name: "IX_Domains_DnsProviderAccountId", table: "Domains", column: "DnsProviderAccountId");
		m.CreateIndex(name: "IX_Domains_PrimaryDomain", table: "Domains", column: "PrimaryDomain");
	}

	protected override void Down(MigrationBuilder m)
	{
		m.DropTable(name: "CertificateLogs");
		m.DropTable(name: "CertificateOrders");
		m.DropTable(name: "Certificates");
		m.DropTable(name: "Domains");
		m.DropTable(name: "DnsProviderAccounts");
		m.DropTable(name: "AcmeAccounts");
	}
}