using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "organizations");
        }
    }
}
