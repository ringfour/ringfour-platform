using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261114130000_ArtifactDeliveryStatus")]
public partial class ArtifactDeliveryStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "artifact_state", table: "script_executions", type: "text", nullable: false, defaultValue: "unknown");
        migrationBuilder.AddColumn<string>(name: "artifact_error", table: "script_executions", type: "jsonb", nullable: true);
        migrationBuilder.AddCheckConstraint(name: "ck_script_execution_artifact_state", table: "script_executions", sql: "artifact_state IN ('unknown', 'pending', 'uploading', 'ready', 'failed')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_script_execution_artifact_state", table: "script_executions");
        migrationBuilder.DropColumn(name: "artifact_state", table: "script_executions");
        migrationBuilder.DropColumn(name: "artifact_error", table: "script_executions");
    }
}
