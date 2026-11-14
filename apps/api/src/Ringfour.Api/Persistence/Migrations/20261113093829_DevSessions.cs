using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    active_generation = table.Column<int>(type: "integer", nullable: true),
                    dependency_digest = table.Column<string>(type: "text", nullable: true),
                    last_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_error = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_sessions", x => x.id);
                    table.CheckConstraint("ck_dev_session_closed", "(state IN ('closed', 'expired')) = (closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_dev_session_digest", "dependency_digest IS NULL OR dependency_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_dev_session_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_dev_session_generations", "generation >= 0 AND (active_generation IS NULL OR (active_generation > 0 AND active_generation <= generation))");
                    table.CheckConstraint("ck_dev_session_state", "state IN ('creating', 'ready', 'syncing', 'starting', 'running', 'stopping', 'closing', 'closed', 'expired', 'failed')");
                    table.ForeignKey(
                        name: "FK_dev_sessions_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_sessions_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_session_generations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    archive_sha256 = table.Column<string>(type: "text", nullable: false),
                    archive_size = table.Column<int>(type: "integer", nullable: false),
                    object_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleaned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_session_generations", x => x.id);
                    table.CheckConstraint("ck_dev_generation_archive", "archive_size BETWEEN 1 AND 1048576 AND archive_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_dev_generation_number", "generation > 0");
                    table.CheckConstraint("ck_dev_generation_state", "state IN ('pending', 'committed', 'failed')");
                    table.ForeignKey(
                        name: "FK_dev_session_generations_dev_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "dev_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_generations_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_generations_operation_id",
                table: "dev_session_generations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_generations_session_id",
                table: "dev_session_generations",
                column: "session_id",
                unique: true,
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_generations_session_id_generation",
                table: "dev_session_generations",
                columns: new[] { "session_id", "generation" },
                unique: true,
                filter: "state IN ('pending', 'committed')");

            migrationBuilder.CreateIndex(
                name: "IX_dev_sessions_owner_user_id",
                table: "dev_sessions",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_sessions_robot_id",
                table: "dev_sessions",
                column: "robot_id",
                unique: true,
                filter: "state NOT IN ('closed', 'expired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_session_generations");

            migrationBuilder.DropTable(
                name: "dev_sessions");
        }
    }
}
