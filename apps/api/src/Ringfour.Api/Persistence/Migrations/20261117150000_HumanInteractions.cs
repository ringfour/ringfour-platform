using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

[DbContext(typeof(RingfourDbContext))]
[Migration("20261117150000_HumanInteractions")]
public partial class HumanInteractions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "human_interactions", columns: table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            robot_id = table.Column<Guid>(type: "uuid", nullable: false),
            execution_id = table.Column<Guid>(type: "uuid", nullable: false),
            prompt = table.Column<string>(type: "text", nullable: false),
            choices = table.Column<string>(type: "jsonb", nullable: false),
            deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            timeout_outcome = table.Column<string>(type: "text", nullable: false),
            state = table.Column<string>(type: "text", nullable: false),
            outcome = table.Column<string>(type: "text", nullable: true),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_human_interactions", x => x.id);
            table.ForeignKey("FK_human_interactions_robots_robot_id", x => x.robot_id, "robots", "id", onDelete: ReferentialAction.Restrict);
            table.CheckConstraint("ck_human_interaction_state", "state IN ('pending','answered','timed_out','cancelled')");
            table.CheckConstraint("ck_human_interaction_outcome", "(state = 'pending' AND outcome IS NULL) OR (state IN ('answered','timed_out') AND outcome IS NOT NULL) OR (state = 'cancelled' AND outcome IS NULL)");
        });
        migrationBuilder.CreateTable(name: "human_interaction_responses", columns: table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            interaction_id = table.Column<Guid>(type: "uuid", nullable: false),
            actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
            chosen_value = table.Column<string>(type: "text", nullable: false),
            status = table.Column<string>(type: "text", nullable: false),
            idempotency_key = table.Column<string>(type: "text", nullable: false),
            request_fingerprint = table.Column<string>(type: "text", nullable: false),
            submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_human_interaction_responses", x => x.id);
            table.ForeignKey("FK_human_interaction_responses_human_interactions_interaction_id", x => x.interaction_id, "human_interactions", "id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_human_interaction_responses_users_actor_user_id", x => x.actor_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
            table.CheckConstraint("ck_human_response_status", "status IN ('submitted','applied','rejected_timed_out','rejected_cancelled')");
        });
        migrationBuilder.CreateIndex("IX_human_interactions_execution_id", "human_interactions", "execution_id");
        migrationBuilder.CreateIndex("IX_human_interactions_robot_id_created_at_id", "human_interactions", new[] { "robot_id", "created_at", "id" });
        migrationBuilder.CreateIndex("IX_human_interaction_responses_actor_user_id", "human_interaction_responses", "actor_user_id");
        migrationBuilder.CreateIndex("IX_human_interaction_responses_interaction_id", "human_interaction_responses", "interaction_id", unique: true);
        migrationBuilder.CreateIndex("IX_human_interaction_responses_interaction_id_actor_user_id_idempotency_key", "human_interaction_responses", new[] { "interaction_id", "actor_user_id", "idempotency_key" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("human_interaction_responses");
        migrationBuilder.DropTable("human_interactions");
    }
}
