using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MissionUiSessionTokenIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_mission_ui_sessions_session_token_hash",
                table: "mission_ui_sessions",
                column: "session_token_hash",
                unique: true,
                filter: "session_token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mission_ui_sessions_session_token_hash",
                table: "mission_ui_sessions");
        }
    }
}
