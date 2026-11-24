using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertManagement.Infrastructure.Migrations;

[DbContext(typeof(CertificateDbContext))]
[Migration("202611050001_AddEsaDeployment")]
public partial class AddEsaDeployment : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>(name: "EsaCertificateId", table: "WafDeploymentTargets", type: "TEXT", nullable: true);
        m.AddColumn<string>(name: "EsaCertificateId", table: "CertificateDeployments", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder m)
    {
        throw new NotSupportedException("回退会删除 ESA 证书 ID，请先人工确认数据处理方案。");
    }
}
