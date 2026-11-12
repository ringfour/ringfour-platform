using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MissionDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "deployment_id",
                table: "script_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mission_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    desired_state = table.Column<string>(type: "text", nullable: false),
                    observed_state = table.Column<string>(type: "text", nullable: false),
                    last_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_error = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mission_deployments", x => x.id);
                    table.CheckConstraint("ck_mission_candidate", "(candidate_revision_id IS NULL) = (candidate_operation_id IS NULL)");
                    table.CheckConstraint("ck_mission_desired", "desired_state IN ('stopped', 'running')");
                    table.CheckConstraint("ck_mission_observed", "observed_state IN ('preparing', 'ready', 'starting', 'running', 'stopping', 'succeeded', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_mission_deployments_mission_revisions_candidate_revision_id",
                        column: x => x.candidate_revision_id,
                        principalTable: "mission_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mission_deployments_mission_revisions_selected_revision_id",
                        column: x => x.selected_revision_id,
                        principalTable: "mission_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mission_deployments_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stop_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_actions", x => x.id);
                    table.CheckConstraint("ck_deployment_action_kind", "kind IN ('start', 'stop', 'restart')");
                    table.CheckConstraint("ck_deployment_action_state", "state IN ('pending', 'starting', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_deployment_actions_mission_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "mission_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployment_actions_operations_id",
                        column: x => x.id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployment_actions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_deployment_id",
                table: "script_executions",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_actions_actor_user_id",
                table: "deployment_actions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_actions_deployment_id_actor_user_id_kind_idempot~",
                table: "deployment_actions",
                columns: new[] { "deployment_id", "actor_user_id", "kind", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mission_deployments_candidate_revision_id",
                table: "mission_deployments",
                column: "candidate_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_mission_deployments_robot_id",
                table: "mission_deployments",
                column: "robot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mission_deployments_selected_revision_id",
                table: "mission_deployments",
                column: "selected_revision_id");

            migrationBuilder.AddForeignKey(
                name: "FK_script_executions_mission_deployments_deployment_id",
                table: "script_executions",
                column: "deployment_id",
                principalTable: "mission_deployments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_script_executions_mission_deployments_deployment_id",
                table: "script_executions");

            migrationBuilder.DropTable(
                name: "deployment_actions");

            migrationBuilder.DropTable(
                name: "mission_deployments");

            migrationBuilder.DropIndex(
                name: "IX_script_executions_deployment_id",
                table: "script_executions");

            migrationBuilder.DropColumn(
                name: "deployment_id",
                table: "script_executions");
        }
    }
}
