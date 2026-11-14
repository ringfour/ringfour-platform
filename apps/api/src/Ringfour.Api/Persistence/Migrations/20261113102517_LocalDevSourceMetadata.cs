using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalDevSourceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cleaned_at",
                table: "dev_session_generations");

            migrationBuilder.DropColumn(
                name: "manifest",
                table: "dev_session_generations");

            migrationBuilder.DropColumn(
                name: "object_key",
                table: "dev_session_generations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cleaned_at",
                table: "dev_session_generations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manifest",
                table: "dev_session_generations",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "object_key",
                table: "dev_session_generations",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
