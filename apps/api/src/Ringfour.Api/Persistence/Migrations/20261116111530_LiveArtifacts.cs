using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LiveArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_type",
                table: "script_artifacts",
                type: "text",
                nullable: false,
                defaultValue: "application/octet-stream");

            migrationBuilder.AddColumn<Guid>(
                name: "id",
                table: "script_artifacts",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "script_artifacts",
                type: "text",
                nullable: false,
                defaultValue: "available");

            migrationBuilder.CreateIndex(
                name: "IX_script_artifacts_id",
                table: "script_artifacts",
                column: "id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_script_artifact_content_type",
                table: "script_artifacts",
                sql: "content_type IN ('image/jpeg', 'image/png', 'application/octet-stream')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_script_artifact_state",
                table: "script_artifacts",
                sql: "state IN ('pending', 'uploading', 'available', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_script_artifacts_id",
                table: "script_artifacts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_script_artifact_content_type",
                table: "script_artifacts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_script_artifact_state",
                table: "script_artifacts");

            migrationBuilder.DropColumn(
                name: "content_type",
                table: "script_artifacts");

            migrationBuilder.DropColumn(
                name: "id",
                table: "script_artifacts");

            migrationBuilder.DropColumn(
                name: "state",
                table: "script_artifacts");
        }
    }
}
