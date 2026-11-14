using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentMotionLogbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.AlterColumn<Guid>(
                name: "operation_id",
                table: "logbook_events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.AlterColumn<Guid>(
                name: "operation_id",
                table: "logbook_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "schema_version = 1 AND ((type = 'device.operation.state_changed' AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL))");
        }
    }
}
