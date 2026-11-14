using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogbookAuthorEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "logbook_events",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "execution_id",
                table: "logbook_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload",
                table: "logbook_events",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "schema_version = 1 AND ((type = 'device.operation.state_changed' AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events");

            migrationBuilder.DropColumn(
                name: "execution_id",
                table: "logbook_events");

            migrationBuilder.DropColumn(
                name: "payload",
                table: "logbook_events");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "logbook_events",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_logbook_version",
                table: "logbook_events",
                sql: "schema_version = 1 AND type = 'device.operation.state_changed'");
        }
    }
}
