using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AP17SettingsEndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events",
                sql: "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled','mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped','app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted','app.settings.selected','app.settings.application_requested','app.settings.applied','app.settings.rejected','app.settings.ended_before_confirmation'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL) OR (type IN ('mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped') AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (schema_version = 3 AND (app_operation_id IS NOT NULL OR type = 'app.settings.selected' OR type = 'app.settings.ended_before_confirmation') AND operation_id IS NULL AND app_installation_id IS NOT NULL AND state IS NULL AND payload IS NOT NULL AND local_sequence IS NULL AND type IN ('app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted','app.settings.selected','app.settings.application_requested','app.settings.applied','app.settings.rejected','app.settings.ended_before_confirmation'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events",
                sql: "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled','mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped','app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted','app.settings.selected','app.settings.application_requested','app.settings.applied','app.settings.rejected'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL) OR (type IN ('mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped') AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (schema_version = 3 AND (app_operation_id IS NOT NULL OR type = 'app.settings.selected') AND operation_id IS NULL AND app_installation_id IS NOT NULL AND state IS NULL AND payload IS NOT NULL AND local_sequence IS NULL AND type IN ('app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted','app.settings.selected','app.settings.application_requested','app.settings.applied','app.settings.rejected'))");
        }
    }
}
