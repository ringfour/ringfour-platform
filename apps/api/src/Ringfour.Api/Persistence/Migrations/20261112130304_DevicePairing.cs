using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicePairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_pairing_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    credential_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_pairing_requests", x => x.id);
                    table.CheckConstraint("ck_pairing_claim", "(claimed_at IS NULL AND robot_id IS NULL AND credential_id IS NULL) OR (claimed_at IS NOT NULL AND robot_id IS NOT NULL AND credential_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_device_pairing_requests_robot_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "robot_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_pairing_requests_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_pairing_requests_code_hash",
                table: "device_pairing_requests",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_pairing_requests_credential_hash",
                table: "device_pairing_requests",
                column: "credential_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_pairing_requests_credential_id",
                table: "device_pairing_requests",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairing_requests_expires_at",
                table: "device_pairing_requests",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairing_requests_robot_id",
                table: "device_pairing_requests",
                column: "robot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_pairing_requests");
        }
    }
}
