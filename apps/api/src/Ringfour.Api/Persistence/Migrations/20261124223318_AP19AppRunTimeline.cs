using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AP19AppRunTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_app_runs_organization_active_recent",
                table: "app_runs",
                columns: new[] { "organization_id", "requested_at", "id" },
                descending: new[] { false, true, true },
                filter: "state IN ('accepted','running')");

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_organization_recent",
                table: "app_runs",
                columns: new[] { "organization_id", "requested_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_runs_organization_active_recent",
                table: "app_runs");

            migrationBuilder.DropIndex(
                name: "IX_app_runs_organization_recent",
                table: "app_runs");
        }
    }
}
