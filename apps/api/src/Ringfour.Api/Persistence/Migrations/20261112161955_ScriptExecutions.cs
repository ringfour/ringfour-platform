using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScriptExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "script_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "jsonb", nullable: true),
                    stdout = table.Column<byte[]>(type: "bytea", nullable: false),
                    stderr = table.Column<byte[]>(type: "bytea", nullable: false),
                    stdout_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    stderr_truncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_executions", x => x.id);
                    table.CheckConstraint("ck_script_execution_output", "octet_length(stdout) <= 65536 AND octet_length(stderr) <= 65536");
                    table.CheckConstraint("ck_script_execution_state", "state IN ('queued', 'preparing', 'running', 'succeeded', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_script_executions_operations_id",
                        column: x => x.id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_script_revisions_revision_id",
                        column: x => x.revision_id,
                        principalTable: "script_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "script_artifacts",
                columns: table => new
                {
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_artifacts", x => new { x.execution_id, x.path });
                    table.CheckConstraint("ck_script_artifact_size", "size BETWEEN 0 AND 262144");
                    table.ForeignKey(
                        name: "FK_script_artifacts_script_executions_execution_id",
                        column: x => x.execution_id,
                        principalTable: "script_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_actor_user_id",
                table: "script_executions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_revision_id",
                table: "script_executions",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_robot_id",
                table: "script_executions",
                column: "robot_id",
                unique: true,
                filter: "state IN ('queued', 'preparing', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "script_artifacts");

            migrationBuilder.DropTable(
                name: "script_executions");
        }
    }
}
