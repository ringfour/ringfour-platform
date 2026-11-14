using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevSessionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_session_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_session_runs", x => x.id);
                    table.CheckConstraint("ck_dev_run_generation", "generation BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_dev_run_state", "state IN ('queued','preparing','running','succeeded','failed','cancelled')");
                    table.ForeignKey(
                        name: "FK_dev_session_runs_dev_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "dev_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_runs_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_runs_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_runs_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_actor_user_id",
                table: "dev_session_runs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_operation_id",
                table: "dev_session_runs",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_robot_id_state",
                table: "dev_session_runs",
                columns: new[] { "robot_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_session_id",
                table: "dev_session_runs",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_session_runs");
        }
    }
}
