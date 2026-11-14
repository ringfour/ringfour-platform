using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevLocalGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_local_endpoints",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_sha256 = table.Column<string>(type: "text", nullable: false),
                    daemon_url = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_local_endpoints", x => x.robot_id);
                    table.CheckConstraint("ck_dev_endpoint_fingerprint", "certificate_sha256 ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_dev_local_endpoints_robot_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "robot_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_local_endpoints_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_local_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_sha256 = table.Column<string>(type: "text", nullable: false),
                    daemon_url = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_local_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_local_grants_dev_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "dev_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_local_grants_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_local_endpoints_credential_id",
                table: "dev_local_endpoints",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_local_grants_operation_id",
                table: "dev_local_grants",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_local_grants_session_id",
                table: "dev_local_grants",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_local_endpoints");

            migrationBuilder.DropTable(
                name: "dev_local_grants");
        }
    }
}
