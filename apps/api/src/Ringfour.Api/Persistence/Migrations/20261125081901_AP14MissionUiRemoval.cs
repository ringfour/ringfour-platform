using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AP14MissionUiRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mission_ui_sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mission_ui_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bootstrap_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    bootstrap_token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exchanged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    session_token_hash = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mission_ui_sessions", x => x.id);
                    table.CheckConstraint("ck_mission_ui_session_hashes", "octet_length(bootstrap_token_hash) = 32 AND (session_token_hash IS NULL OR octet_length(session_token_hash) = 32) AND ((session_token_hash IS NULL AND exchanged_at IS NULL) OR (session_token_hash IS NOT NULL AND exchanged_at IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_mission_ui_sessions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mission_ui_sessions_script_executions_execution_id",
                        column: x => x.execution_id,
                        principalTable: "script_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mission_ui_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mission_ui_sessions_execution_id_user_id_expires_at",
                table: "mission_ui_sessions",
                columns: new[] { "execution_id", "user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_mission_ui_sessions_organization_id",
                table: "mission_ui_sessions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_mission_ui_sessions_session_token_hash",
                table: "mission_ui_sessions",
                column: "session_token_hash",
                unique: true,
                filter: "session_token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mission_ui_sessions_user_id",
                table: "mission_ui_sessions",
                column: "user_id");
        }
    }
}
