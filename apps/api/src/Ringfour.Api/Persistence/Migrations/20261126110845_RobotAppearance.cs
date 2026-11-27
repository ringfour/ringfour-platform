using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RobotAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events");

            migrationBuilder.AddColumn<string>(
                name: "appearance",
                table: "robots",
                type: "text",
                nullable: true);

            // Owner-requested initial image for virt2. Do not guess if names are ambiguous.
            migrationBuilder.Sql("UPDATE robots SET appearance = 'base101' WHERE name = 'virt2' AND appearance IS NULL AND (SELECT count(*) FROM robots WHERE name = 'virt2') = 1;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events",
                sql: "type IN ('robot_created', 'appearance_updated', 'enrollment_token_issued', 'enrolled', 'credential_rotated', 'credential_revoked')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events");

            migrationBuilder.Sql("DELETE FROM robot_audit_events WHERE type = 'appearance_updated';");

            migrationBuilder.DropColumn(
                name: "appearance",
                table: "robots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events",
                sql: "type IN ('robot_created', 'enrollment_token_issued', 'enrolled', 'credential_rotated', 'credential_revoked')");
        }
    }
}
