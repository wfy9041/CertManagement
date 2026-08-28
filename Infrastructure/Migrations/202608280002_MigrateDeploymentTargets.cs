using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertManagement.Infrastructure.Migrations;

[DbContext(typeof(CertificateDbContext))]
[Migration("202608280002_MigrateDeploymentTargets")]
public partial class MigrateDeploymentTargets : Migration
{
	protected override void Up(MigrationBuilder m)
	{
		m.Sql("ALTER TABLE WafDeploymentTargets ADD COLUMN DomainId INTEGER NOT NULL DEFAULT 0;");
		m.Sql("ALTER TABLE WafDeploymentTargets ADD COLUMN Type TEXT NOT NULL DEFAULT 'Waf';");
		m.Sql("ALTER TABLE WafDeploymentTargets ADD COLUMN WafDomain TEXT NOT NULL DEFAULT '';");
		m.Sql("CREATE INDEX IX_WafDeploymentTargets_DomainId ON WafDeploymentTargets (DomainId);");
	}

	protected override void Down(MigrationBuilder m)
	{
	}
}