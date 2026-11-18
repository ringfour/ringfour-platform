using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261117170000_IndefiniteInteractions")]
public partial class IndefiniteInteractions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_human_interaction_outcome",
            table: "human_interactions");
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "deadline",
            table: "human_interactions",
            type: "timestamp with time zone",
            nullable: true,
            oldClrType: typeof(DateTimeOffset),
            oldType: "timestamp with time zone");
        migrationBuilder.AlterColumn<string>(
            name: "timeout_outcome",
            table: "human_interactions",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text");
        migrationBuilder.AddCheckConstraint(
            name: "ck_human_interaction_timeout",
            table: "human_interactions",
            sql: "(deadline IS NULL AND timeout_outcome IS NULL) OR (deadline IS NOT NULL AND timeout_outcome IS NOT NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_human_interaction_outcome",
            table: "human_interactions",
            sql: "(state = 'pending' AND outcome IS NULL) OR (state = 'answered' AND outcome IS NOT NULL) OR (state = 'timed_out' AND outcome IS NOT NULL AND deadline IS NOT NULL AND timeout_outcome IS NOT NULL AND outcome = timeout_outcome) OR (state = 'cancelled' AND outcome IS NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_human_interaction_timeout",
            table: "human_interactions");
        migrationBuilder.DropCheckConstraint(
            name: "ck_human_interaction_outcome",
            table: "human_interactions");
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "deadline",
            table: "human_interactions",
            type: "timestamp with time zone",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "timestamp with time zone",
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "timeout_outcome",
            table: "human_interactions",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);
        migrationBuilder.AddCheckConstraint(
            name: "ck_human_interaction_outcome",
            table: "human_interactions",
            sql: "(state = 'pending' AND outcome IS NULL) OR (state IN ('answered','timed_out') AND outcome IS NOT NULL) OR (state = 'cancelled' AND outcome IS NULL)");
    }
}
