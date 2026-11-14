using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogbookIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "logbook_checkpoints",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logbook_checkpoints", x => x.robot_id);
                    table.CheckConstraint("ck_logbook_checkpoint", "sequence BETWEEN 0 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_logbook_checkpoints_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "logbook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_sequence = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logbook_events", x => x.id);
                    table.CheckConstraint("ck_logbook_sequence", "local_sequence BETWEEN 1 AND 9007199254740991");
                    table.CheckConstraint("ck_logbook_state", "state IN ('accepted','running','succeeded','failed','cancelled')");
                    table.CheckConstraint("ck_logbook_version", "schema_version = 1 AND type = 'device.operation.state_changed'");
                    table.ForeignKey(
                        name: "FK_logbook_events_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_logbook_events_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logbook_events_operation_id",
                table: "logbook_events",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_logbook_events_robot_id_local_sequence",
                table: "logbook_events",
                columns: new[] { "robot_id", "local_sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "logbook_checkpoints");

            migrationBuilder.DropTable(
                name: "logbook_events");
        }
    }
}
