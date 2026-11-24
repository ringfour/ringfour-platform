using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppRuntimeEvidence : Migration
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

            migrationBuilder.AddColumn<Guid>(
                name: "app_installation_id",
                table: "logbook_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "app_operation_id",
                table: "logbook_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "app_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_artifacts_app_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "app_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_run_outputs",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stdout = table.Column<byte[]>(type: "bytea", nullable: false),
                    stderr = table.Column<byte[]>(type: "bytea", nullable: false),
                    stdout_sealed = table.Column<bool>(type: "boolean", nullable: false),
                    stderr_sealed = table.Column<bool>(type: "boolean", nullable: false),
                    stdout_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    stderr_truncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_run_outputs", x => x.run_id);
                    table.CheckConstraint("ck_app_output_bounds", "octet_length(stdout) <= 65536 AND octet_length(stderr) <= 65536");
                    table.ForeignKey(
                        name: "FK_app_run_outputs_app_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "app_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logbook_events_app_installation_id",
                table: "logbook_events",
                column: "app_installation_id");

            migrationBuilder.CreateIndex(
                name: "IX_logbook_events_app_operation_id",
                table: "logbook_events",
                column: "app_operation_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events",
                sql: "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled','mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped','app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL) OR (type IN ('mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped') AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (schema_version = 3 AND app_operation_id IS NOT NULL AND operation_id IS NULL AND app_installation_id IS NOT NULL AND state IS NULL AND payload IS NOT NULL AND local_sequence IS NULL AND type IN ('app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted'))");

            migrationBuilder.CreateIndex(
                name: "IX_app_artifacts_run_id_path",
                table: "app_artifacts",
                columns: new[] { "run_id", "path" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_logbook_events_app_installations_app_installation_id",
                table: "logbook_events",
                column: "app_installation_id",
                principalTable: "app_installations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_logbook_events_app_operations_app_operation_id",
                table: "logbook_events",
                column: "app_operation_id",
                principalTable: "app_operations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_logbook_events_app_installations_app_installation_id",
                table: "logbook_events");

            migrationBuilder.DropForeignKey(
                name: "FK_logbook_events_app_operations_app_operation_id",
                table: "logbook_events");

            migrationBuilder.DropTable(
                name: "app_artifacts");

            migrationBuilder.DropTable(
                name: "app_run_outputs");

            migrationBuilder.DropIndex(
                name: "IX_logbook_events_app_installation_id",
                table: "logbook_events");

            migrationBuilder.DropIndex(
                name: "IX_logbook_events_app_operation_id",
                table: "logbook_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.DropColumn(
                name: "app_installation_id",
                table: "logbook_events");

            migrationBuilder.DropColumn(
                name: "app_operation_id",
                table: "logbook_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_sequence",
                table: "logbook_events",
                sql: "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled','mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL) OR (type IN ('mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped') AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL)");
        }
    }
}
