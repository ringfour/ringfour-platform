using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppRunDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_operations_installation_id",
                table: "app_operations");

            migrationBuilder.AddColumn<bool>(
                name: "runs_supported",
                table: "robot_app_inventory",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_installation_id",
                table: "app_operations",
                column: "installation_id",
                unique: true,
                filter: "state IN ('queued','accepted','running') AND run_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_operations_installation_id",
                table: "app_operations");

            migrationBuilder.DropColumn(
                name: "runs_supported",
                table: "robot_app_inventory");

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_installation_id",
                table: "app_operations",
                column: "installation_id",
                unique: true,
                filter: "state IN ('queued','accepted','running')");
        }
    }
}
