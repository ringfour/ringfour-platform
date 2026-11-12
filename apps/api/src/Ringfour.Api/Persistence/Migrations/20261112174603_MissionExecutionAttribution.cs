using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MissionExecutionAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "mission_revision_id",
                table: "script_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_mission_revision_id",
                table: "script_executions",
                column: "mission_revision_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_mission_attribution",
                table: "script_executions",
                sql: "(deployment_id IS NULL) = (mission_revision_id IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_script_executions_mission_revisions_mission_revision_id",
                table: "script_executions",
                column: "mission_revision_id",
                principalTable: "mission_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_script_executions_mission_revisions_mission_revision_id",
                table: "script_executions");

            migrationBuilder.DropIndex(
                name: "IX_script_executions_mission_revision_id",
                table: "script_executions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_mission_attribution",
                table: "script_executions");

            migrationBuilder.DropColumn(
                name: "mission_revision_id",
                table: "script_executions");
        }
    }
}
