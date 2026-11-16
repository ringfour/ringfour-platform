using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261114110000_RemoveDevGenerationLimit")]
public partial class RemoveDevGenerationLimit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_dev_run_generation", table: "dev_session_runs");
        migrationBuilder.AddCheckConstraint(name: "ck_dev_run_generation", table: "dev_session_runs", sql: "generation > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_dev_run_generation", table: "dev_session_runs");
        migrationBuilder.AddCheckConstraint(name: "ck_dev_run_generation", table: "dev_session_runs", sql: "generation BETWEEN 1 AND 20");
    }
}
