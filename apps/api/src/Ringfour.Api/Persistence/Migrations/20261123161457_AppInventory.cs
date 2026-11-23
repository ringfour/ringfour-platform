using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_device_reports",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_namespace = table.Column<Guid>(type: "uuid", nullable: false),
                    route = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    body_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    receipt = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_device_reports", x => new { x.robot_id, x.report_id });
                    table.CheckConstraint("ck_app_device_report_sequence", "sequence BETWEEN 1 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_app_device_reports_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "robot_app_inventory",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_namespace = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offered_versions = table.Column<string>(type: "jsonb", nullable: false),
                    supported = table.Column<bool>(type: "boolean", nullable: false),
                    negotiated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    report_sequence = table.Column<long>(type: "bigint", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    platform = table.Column<string>(type: "jsonb", nullable: true),
                    inventory = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_robot_app_inventory", x => x.robot_id);
                    table.CheckConstraint("ck_app_inventory_projection", "(report_sequence = 0 AND received_at IS NULL AND platform IS NULL AND inventory IS NULL) OR (report_sequence > 0 AND received_at IS NOT NULL AND platform IS NOT NULL AND inventory IS NOT NULL)");
                    table.CheckConstraint("ck_app_inventory_sequence", "report_sequence BETWEEN 0 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_robot_app_inventory_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_device_reports_robot_id_deployment_namespace_route_sequ~",
                table: "app_device_reports",
                columns: new[] { "robot_id", "deployment_namespace", "route", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_device_reports");

            migrationBuilder.DropTable(
                name: "robot_app_inventory");
        }
    }
}
