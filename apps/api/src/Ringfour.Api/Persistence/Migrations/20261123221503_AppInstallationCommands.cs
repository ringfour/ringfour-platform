using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppInstallationCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "installations_supported",
                table: "robot_app_inventory",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "command",
                table: "app_operations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dispatched_at",
                table: "app_operations",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION protect_app_command() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.command IS DISTINCT FROM OLD.command OR
                       (OLD.dispatched_at IS NOT NULL AND NEW.dispatched_at IS DISTINCT FROM OLD.dispatched_at) THEN
                        RAISE EXCEPTION 'App command and dispatch evidence are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_app_command_immutable BEFORE UPDATE ON app_operations
                FOR EACH ROW EXECUTE FUNCTION protect_app_command();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_app_command_immutable ON app_operations; DROP FUNCTION protect_app_command();");
            migrationBuilder.DropColumn(
                name: "installations_supported",
                table: "robot_app_inventory");

            migrationBuilder.DropColumn(
                name: "command",
                table: "app_operations");

            migrationBuilder.DropColumn(
                name: "dispatched_at",
                table: "app_operations");
        }
    }
}
