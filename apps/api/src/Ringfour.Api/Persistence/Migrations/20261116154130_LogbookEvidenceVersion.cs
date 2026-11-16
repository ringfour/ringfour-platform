using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

public partial class LogbookEvidenceVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_logbook_version",
            table: "logbook_events");

        migrationBuilder.AddCheckConstraint(
            name: "ck_logbook_version",
            table: "logbook_events",
            sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_logbook_version",
            table: "logbook_events");

        migrationBuilder.AddCheckConstraint(
            name: "ck_logbook_version",
            table: "logbook_events",
            sql: "schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL))");
    }
}
