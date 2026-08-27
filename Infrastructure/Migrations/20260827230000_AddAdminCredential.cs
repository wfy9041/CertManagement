using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertManagement.Infrastructure.Migrations;

[DbContext(typeof(CertManagement.Infrastructure.CertificateDbContext))]
[Migration("20260827230000_AddAdminCredential")]
public partial class AddAdminCredential : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminCredentials",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
                Username = table.Column<string>(nullable: false),
                PasswordHash = table.Column<string>(nullable: false),
                UpdatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AdminCredentials", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AdminCredentials_Username",
            table: "AdminCredentials",
            column: "Username",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AdminCredentials");
    }
}
