using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261114120000_LargeObjectSizes")]
public partial class LargeObjectSizes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_script_revision_size", table: "script_revisions");
        migrationBuilder.DropCheckConstraint(name: "ck_script_artifact_size", table: "script_artifacts");
        migrationBuilder.AlterColumn<long>(name: "archive_size", table: "script_revisions", type: "bigint", nullable: false, oldClrType: typeof(int), oldType: "integer");
        migrationBuilder.AlterColumn<long>(name: "size", table: "script_artifacts", type: "bigint", nullable: false, oldClrType: typeof(int), oldType: "integer");
        migrationBuilder.AddCheckConstraint(name: "ck_script_revision_size", table: "script_revisions", sql: "archive_size BETWEEN 1 AND 9007199254740991");
        migrationBuilder.AddCheckConstraint(name: "ck_script_artifact_size", table: "script_artifacts", sql: "size BETWEEN 0 AND 9007199254740991");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_script_revision_size", table: "script_revisions");
        migrationBuilder.DropCheckConstraint(name: "ck_script_artifact_size", table: "script_artifacts");
        migrationBuilder.AlterColumn<int>(name: "archive_size", table: "script_revisions", type: "integer", nullable: false, oldClrType: typeof(long), oldType: "bigint");
        migrationBuilder.AlterColumn<int>(name: "size", table: "script_artifacts", type: "integer", nullable: false, oldClrType: typeof(long), oldType: "bigint");
        migrationBuilder.AddCheckConstraint(name: "ck_script_revision_size", table: "script_revisions", sql: "archive_size BETWEEN 1 AND 1048576");
        migrationBuilder.AddCheckConstraint(name: "ck_script_artifact_size", table: "script_artifacts", sql: "size BETWEEN 0 AND 262144");
    }
}
