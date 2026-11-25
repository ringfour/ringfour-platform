using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AP15LegacyWorkflowRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_logbook_events_operations_operation_id",
                table: "logbook_events");

            migrationBuilder.DropTable(
                name: "deployment_actions");

            migrationBuilder.DropTable(
                name: "dev_local_endpoints");

            migrationBuilder.DropTable(
                name: "dev_local_grants");

            migrationBuilder.DropTable(
                name: "dev_session_generations");

            migrationBuilder.DropTable(
                name: "dev_session_runs");

            migrationBuilder.DropTable(
                name: "operation_idempotency_records");

            migrationBuilder.DropTable(
                name: "script_artifacts");

            migrationBuilder.DropTable(
                name: "script_execution_events");

            migrationBuilder.DropTable(
                name: "dev_sessions");

            migrationBuilder.DropTable(
                name: "script_executions");

            migrationBuilder.DropTable(
                name: "mission_deployments");

            migrationBuilder.DropTable(
                name: "operations");

            migrationBuilder.DropTable(
                name: "mission_revisions");

            migrationBuilder.DropTable(
                name: "missions");

            migrationBuilder.DropTable(
                name: "script_revisions");

            migrationBuilder.DropIndex(
                name: "IX_logbook_events_operation_id",
                table: "logbook_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_local_endpoints",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_sha256 = table.Column<string>(type: "text", nullable: false),
                    daemon_url = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_local_endpoints", x => x.robot_id);
                    table.CheckConstraint("ck_dev_endpoint_fingerprint", "certificate_sha256 ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_dev_local_endpoints_robot_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "robot_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_local_endpoints_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active_generation = table.Column<int>(type: "integer", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dependency_digest = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "jsonb", nullable: true),
                    last_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_sessions", x => x.id);
                    table.CheckConstraint("ck_dev_session_closed", "(state IN ('closed', 'expired')) = (closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_dev_session_digest", "dependency_digest IS NULL OR dependency_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_dev_session_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_dev_session_generations", "generation >= 0 AND (active_generation IS NULL OR (active_generation > 0 AND active_generation <= generation))");
                    table.CheckConstraint("ck_dev_session_state", "state IN ('creating', 'ready', 'syncing', 'starting', 'running', 'stopping', 'closing', 'closed', 'expired', 'failed')");
                    table.ForeignKey(
                        name: "FK_dev_sessions_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_sessions_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    latest_version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_missions", x => x.id);
                    table.CheckConstraint("ck_mission_version", "latest_version >= 0");
                    table.ForeignKey(
                        name: "FK_missions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_missions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_version = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations", x => x.id);
                    table.CheckConstraint("ck_operations_state", "state IN ('Queued', 'Accepted', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_operations_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operations_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "script_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archive_sha256 = table.Column<string>(type: "text", nullable: false),
                    archive_size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    entrypoint = table.Column<string>(type: "text", nullable: false),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    lock_sha256 = table.Column<string>(type: "text", nullable: false),
                    python_requirement = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    revision_digest = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    upload_cleaned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_revisions", x => x.id);
                    table.CheckConstraint("ck_script_revision_size", "archive_size BETWEEN 1 AND 9007199254740991");
                    table.CheckConstraint("ck_script_revision_state", "(state = 'uploading' AND finalized_at IS NULL) OR (state = 'ready' AND finalized_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_script_revisions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_revisions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_local_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_sha256 = table.Column<string>(type: "text", nullable: false),
                    daemon_url = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_local_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_dev_local_grants_dev_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "dev_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_local_grants_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_session_generations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archive_sha256 = table.Column<string>(type: "text", nullable: false),
                    archive_size = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_session_generations", x => x.id);
                    table.CheckConstraint("ck_dev_generation_archive", "archive_size BETWEEN 1 AND 1048576 AND archive_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_dev_generation_number", "generation > 0");
                    table.CheckConstraint("ck_dev_generation_state", "state IN ('pending', 'committed', 'failed')");
                    table.ForeignKey(
                        name: "FK_dev_session_generations_dev_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "dev_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_generations_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_session_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error = table.Column<string>(type: "jsonb", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_session_runs", x => x.id);
                    table.CheckConstraint("ck_dev_run_generation", "generation > 0");
                    table.CheckConstraint("ck_dev_run_state", "state IN ('queued','preparing','running','succeeded','failed','cancelled')");
                    table.ForeignKey(
                        name: "FK_dev_session_runs_dev_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "dev_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_runs_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_runs_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_session_runs_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operation_idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    route = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_idempotency_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_operation_idempotency_records_operations_outcome_operation_~",
                        column: x => x.outcome_operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operation_idempotency_records_users_principal_user_id",
                        column: x => x.principal_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mission_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    script_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mission_revisions", x => x.id);
                    table.CheckConstraint("ck_mission_revision_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_mission_revisions_missions_mission_id",
                        column: x => x.mission_id,
                        principalTable: "missions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mission_revisions_script_revisions_script_revision_id",
                        column: x => x.script_revision_id,
                        principalTable: "script_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mission_revisions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mission_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    desired_state = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "jsonb", nullable: true),
                    last_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    observed_state = table.Column<string>(type: "text", nullable: false),
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
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    stop_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "script_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mission_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_error = table.Column<string>(type: "jsonb", nullable: true),
                    artifact_state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error = table.Column<string>(type: "jsonb", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    stderr = table.Column<byte[]>(type: "bytea", nullable: false),
                    stderr_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    stdout = table.Column<byte[]>(type: "bytea", nullable: false),
                    stdout_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_executions", x => x.id);
                    table.CheckConstraint("ck_execution_mission_attribution", "(deployment_id IS NULL) = (mission_revision_id IS NULL)");
                    table.CheckConstraint("ck_script_execution_artifact_state", "artifact_state IN ('unknown', 'pending', 'uploading', 'ready', 'failed')");
                    table.CheckConstraint("ck_script_execution_output", "octet_length(stdout) <= 65536 AND octet_length(stderr) <= 65536");
                    table.CheckConstraint("ck_script_execution_state", "state IN ('queued', 'preparing', 'running', 'succeeded', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_script_executions_mission_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "mission_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_mission_revisions_mission_revision_id",
                        column: x => x.mission_revision_id,
                        principalTable: "mission_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_operations_id",
                        column: x => x.id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_script_revisions_revision_id",
                        column: x => x.revision_id,
                        principalTable: "script_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_executions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "script_artifacts",
                columns: table => new
                {
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_artifacts", x => new { x.execution_id, x.path });
                    table.CheckConstraint("ck_script_artifact_content_type", "content_type IN ('image/jpeg', 'image/png', 'application/octet-stream')");
                    table.CheckConstraint("ck_script_artifact_size", "size BETWEEN 0 AND 9007199254740991");
                    table.CheckConstraint("ck_script_artifact_state", "state IN ('pending', 'uploading', 'available', 'failed')");
                    table.ForeignKey(
                        name: "FK_script_artifacts_script_executions_execution_id",
                        column: x => x.execution_id,
                        principalTable: "script_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "script_execution_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_execution_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_script_execution_events_script_executions_execution_id",
                        column: x => x.execution_id,
                        principalTable: "script_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_script_execution_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logbook_events_operation_id",
                table: "logbook_events",
                column: "operation_id");

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
                name: "IX_dev_local_endpoints_credential_id",
                table: "dev_local_endpoints",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_local_grants_operation_id",
                table: "dev_local_grants",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_local_grants_session_id",
                table: "dev_local_grants",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_generations_operation_id",
                table: "dev_session_generations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_generations_session_id",
                table: "dev_session_generations",
                column: "session_id",
                unique: true,
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_generations_session_id_generation",
                table: "dev_session_generations",
                columns: new[] { "session_id", "generation" },
                unique: true,
                filter: "state IN ('pending', 'committed')");

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_actor_user_id",
                table: "dev_session_runs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_operation_id",
                table: "dev_session_runs",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_robot_id_state",
                table: "dev_session_runs",
                columns: new[] { "robot_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_session_runs_session_id",
                table: "dev_session_runs",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_sessions_owner_user_id",
                table: "dev_sessions",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_sessions_robot_id",
                table: "dev_sessions",
                column: "robot_id",
                unique: true,
                filter: "state NOT IN ('closed', 'expired')");

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

            migrationBuilder.CreateIndex(
                name: "IX_mission_revisions_created_by_user_id",
                table: "mission_revisions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_mission_revisions_mission_id_created_by_user_id_idempotency~",
                table: "mission_revisions",
                columns: new[] { "mission_id", "created_by_user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mission_revisions_mission_id_version",
                table: "mission_revisions",
                columns: new[] { "mission_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mission_revisions_script_revision_id",
                table: "mission_revisions",
                column: "script_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_missions_created_by_user_id_organization_id_idempotency_key",
                table: "missions",
                columns: new[] { "created_by_user_id", "organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_missions_organization_id_name",
                table: "missions",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operation_idempotency_records_outcome_operation_id",
                table: "operation_idempotency_records",
                column: "outcome_operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_operation_idempotency_records_principal_user_id_route_targe~",
                table: "operation_idempotency_records",
                columns: new[] { "principal_user_id", "route", "target", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operations_requested_by_user_id",
                table: "operations",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operations_robot_id_state_created_at_id",
                table: "operations",
                columns: new[] { "robot_id", "state", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_script_artifacts_id",
                table: "script_artifacts",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_script_execution_events_actor_user_id",
                table: "script_execution_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_execution_events_execution_id_state",
                table: "script_execution_events",
                columns: new[] { "execution_id", "state" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_actor_user_id",
                table: "script_executions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_deployment_id",
                table: "script_executions",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_mission_revision_id",
                table: "script_executions",
                column: "mission_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_revision_id",
                table: "script_executions",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_script_executions_robot_id",
                table: "script_executions",
                column: "robot_id",
                unique: true,
                filter: "state IN ('queued', 'preparing', 'running')");

            migrationBuilder.CreateIndex(
                name: "IX_script_revisions_created_by_user_id_organization_id_idempot~",
                table: "script_revisions",
                columns: new[] { "created_by_user_id", "organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_script_revisions_organization_id",
                table: "script_revisions",
                column: "organization_id");

            migrationBuilder.AddForeignKey(
                name: "FK_logbook_events_operations_operation_id",
                table: "logbook_events",
                column: "operation_id",
                principalTable: "operations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
