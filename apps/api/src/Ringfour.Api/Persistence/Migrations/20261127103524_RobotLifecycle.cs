using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RobotLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "robots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "released_at",
                table: "robots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events",
                sql: "type IN ('robot_created', 'appearance_updated', 'enrollment_token_issued', 'enrolled', 'credential_rotated', 'credential_revoked', 'robot_unenrolled', 'robot_deleted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "robots");

            migrationBuilder.DropColumn(
                name: "released_at",
                table: "robots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_robot_audit_events_type",
                table: "robot_audit_events",
                sql: "type IN ('robot_created', 'appearance_updated', 'enrollment_token_issued', 'enrolled', 'credential_rotated', 'credential_revoked')");
        }
    }
}
