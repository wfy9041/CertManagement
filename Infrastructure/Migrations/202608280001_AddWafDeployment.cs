using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace CertManagement.Infrastructure.Migrations;

[DbContext(typeof(CertificateDbContext))]
[Migration("202608280001_AddWafDeployment")]
public partial class AddWafDeployment : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("ALTER TABLE Domains ADD COLUMN WafDeploymentTargetId INTEGER NULL;"); m.Sql("ALTER TABLE Domains ADD COLUMN WafDomain TEXT NULL;");
        m.Sql("CREATE TABLE WafDeploymentTargets (Id INTEGER NOT NULL CONSTRAINT PK_WafDeploymentTargets PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, AccessKeyId TEXT NOT NULL, ProtectedAccessKeySecret TEXT NOT NULL, InstanceId TEXT NOT NULL, Endpoint TEXT NOT NULL, Enabled INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);");
        m.Sql("CREATE TABLE CertificateDeployments (Id INTEGER NOT NULL CONSTRAINT PK_CertificateDeployments PRIMARY KEY AUTOINCREMENT, CertificateId INTEGER NOT NULL, WafDeploymentTargetId INTEGER NOT NULL, WafDomain TEXT NOT NULL, WafCertificateId INTEGER NULL, Status TEXT NOT NULL, RequestId TEXT NULL, ErrorMessage TEXT NULL, CreatedAt TEXT NOT NULL, CompletedAt TEXT NULL, CONSTRAINT FK_CertificateDeployments_Certificates_CertificateId FOREIGN KEY (CertificateId) REFERENCES Certificates (Id) ON DELETE CASCADE, CONSTRAINT FK_CertificateDeployments_WafDeploymentTargets_WafDeploymentTargetId FOREIGN KEY (WafDeploymentTargetId) REFERENCES WafDeploymentTargets (Id) ON DELETE RESTRICT);");
        m.Sql("CREATE INDEX IX_CertificateDeployments_CertificateId_WafDeploymentTargetId ON CertificateDeployments (CertificateId, WafDeploymentTargetId);");
    }
    protected override void Down(MigrationBuilder m) { m.Sql("DROP TABLE CertificateDeployments;"); m.Sql("DROP TABLE WafDeploymentTargets;"); }
}
