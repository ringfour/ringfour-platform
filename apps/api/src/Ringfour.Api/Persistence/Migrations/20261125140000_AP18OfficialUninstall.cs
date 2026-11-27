using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261125140000_AP18OfficialUninstall")]
public sealed class AP18OfficialUninstall : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>("removed_at", "app_installations", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<bool>("uninstall_supported", "robot_app_inventory", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.DropIndex("IX_app_installations_robot_id_app_id", "app_installations");
        migrationBuilder.CreateIndex("IX_app_installations_robot_id_app_id", "app_installations", new[] { "robot_id", "app_id" }, unique: true, filter: "removed_at IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_app_installations_robot_id_app_id", "app_installations");
        migrationBuilder.CreateIndex("IX_app_installations_robot_id_app_id", "app_installations", new[] { "robot_id", "app_id" }, unique: true);
        migrationBuilder.DropColumn("removed_at", "app_installations");
        migrationBuilder.DropColumn("uninstall_supported", "robot_app_inventory");
    }
}
