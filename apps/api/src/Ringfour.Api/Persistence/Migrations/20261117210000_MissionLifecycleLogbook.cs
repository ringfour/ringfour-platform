using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261117210000_MissionLifecycleLogbook")]
public partial class MissionLifecycleLogbook : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_logbook_sequence", "logbook_events");
        migrationBuilder.DropCheckConstraint("ck_logbook_version", "logbook_events");
        migrationBuilder.AddCheckConstraint(name: "ck_logbook_sequence", table: "logbook_events",
            sql: "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled','mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped'))");
        migrationBuilder.AddCheckConstraint(name: "ck_logbook_version", table: "logbook_events",
            sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL) OR (type IN ('mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped') AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_logbook_sequence", "logbook_events");
        migrationBuilder.DropCheckConstraint("ck_logbook_version", "logbook_events");
        migrationBuilder.AddCheckConstraint(name: "ck_logbook_sequence", table: "logbook_events",
            sql: "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled'))");
        migrationBuilder.AddCheckConstraint(name: "ck_logbook_version", table: "logbook_events",
            sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL)");
    }
}
