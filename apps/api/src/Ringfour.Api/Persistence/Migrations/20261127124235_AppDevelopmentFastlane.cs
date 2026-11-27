using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppDevelopmentFastlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_development_endpoints",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    daemon_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    certificate_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_development_endpoints", x => x.robot_id);
                    table.ForeignKey(
                        name: "FK_app_development_endpoints_robot_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "robot_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_development_endpoints_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_development_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    source_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archive_sha256 = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    grant_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    grant_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_development_sessions", x => x.id);
                    table.CheckConstraint("ck_app_development_generation", "generation >= 0");
                    table.ForeignKey(
                        name: "FK_app_development_sessions_app_installations_installation_id",
                        column: x => x.installation_id,
                        principalTable: "app_installations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_development_sessions_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_development_sessions_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_development_endpoints_credential_id",
                table: "app_development_endpoints",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_development_sessions_installation_id",
                table: "app_development_sessions",
                column: "installation_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_development_sessions_owner_user_id",
                table: "app_development_sessions",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_development_sessions_robot_id",
                table: "app_development_sessions",
                column: "robot_id",
                unique: true,
                filter: "closed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_development_endpoints");

            migrationBuilder.DropTable(
                name: "app_development_sessions");
        }
    }
}
