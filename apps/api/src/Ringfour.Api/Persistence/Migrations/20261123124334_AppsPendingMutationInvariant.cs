using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppsPendingMutationInvariant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_operations_installation_id_state",
                table: "app_operations");

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_installation_id",
                table: "app_operations",
                column: "installation_id",
                unique: true,
                filter: "state IN ('queued','accepted','running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_operations_installation_id",
                table: "app_operations");

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_installation_id_state",
                table: "app_operations",
                columns: new[] { "installation_id", "state" },
                filter: "state IN ('queued','accepted','running')");
        }
    }
}
