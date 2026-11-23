using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppsPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_robots_id_organization_id",
                table: "robots",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.CreateTable(
                name: "app_idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_idempotency_records", x => x.id);
                    table.CheckConstraint("ck_app_idempotency_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_app_idempotency_key", "length(key) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_app_idempotency_outcome_object", "jsonb_typeof(outcome) = 'object'");
                    table.ForeignKey(
                        name: "FK_app_idempotency_records_users_principal_user_id",
                        column: x => x.principal_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    publisher_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.id);
                    table.UniqueConstraint("AK_apps_id_publisher_organization_id", x => new { x.id, x.publisher_organization_id });
                    table.CheckConstraint("ck_app_key", "app_key ~ '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$'");
                    table.ForeignKey(
                        name: "FK_apps_organizations_publisher_organization_id",
                        column: x => x.publisher_organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_apps_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "robot_execution_slots",
                columns: table => new
                {
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_robot_execution_slots", x => x.robot_id);
                    table.CheckConstraint("ck_robot_execution_slot_source", "source IN ('legacy','app')");
                    table.ForeignKey(
                        name: "FK_robot_execution_slots_robots_robot_id",
                        column: x => x.robot_id,
                        principalTable: "robots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    publisher_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    manifest_digest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_revisions", x => x.id);
                    table.UniqueConstraint("AK_app_revisions_id_app_id", x => new { x.id, x.app_id });
                    table.UniqueConstraint("AK_app_revisions_id_app_id_publisher_organization_id", x => new { x.id, x.app_id, x.publisher_organization_id });
                    table.CheckConstraint("ck_app_revision_digest", "manifest_digest ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_app_revision_manifest_object", "jsonb_typeof(manifest) = 'object'");
                    table.CheckConstraint("ck_app_revision_visibility", "visibility IN ('listed','unlisted')");
                    table.ForeignKey(
                        name: "FK_app_revisions_apps_app_id_publisher_organization_id",
                        columns: x => new { x.app_id, x.publisher_organization_id },
                        principalTable: "apps",
                        principalColumns: new[] { "id", "publisher_organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_revisions_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    publisher_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_shares", x => x.id);
                    table.UniqueConstraint("AK_app_shares_id_revision_id_app_id_publisher_organization_id_~", x => new { x.id, x.revision_id, x.app_id, x.publisher_organization_id, x.destination_organization_id });
                    table.CheckConstraint("ck_app_share_expiry", "expires_at > granted_at AND (revoked_at IS NULL OR revoked_at >= granted_at)");
                    table.ForeignKey(
                        name: "FK_app_shares_app_revisions_revision_id_app_id_publisher_organ~",
                        columns: x => new { x.revision_id, x.app_id, x.publisher_organization_id },
                        principalTable: "app_revisions",
                        principalColumns: new[] { "id", "app_id", "publisher_organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_shares_organizations_destination_organization_id",
                        column: x => x.destination_organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_shares_users_granted_by_user_id",
                        column: x => x.granted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    publisher_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    share_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_imports", x => x.id);
                    table.UniqueConstraint("AK_app_imports_id_app_id_organization_id", x => new { x.id, x.app_id, x.organization_id });
                    table.CheckConstraint("ck_app_import_source", "(source_kind = 'store' AND share_id IS NULL) OR (source_kind = 'direct_share' AND share_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_app_imports_app_revisions_revision_id_app_id_publisher_orga~",
                        columns: x => new { x.revision_id, x.app_id, x.publisher_organization_id },
                        principalTable: "app_revisions",
                        principalColumns: new[] { "id", "app_id", "publisher_organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_imports_app_shares_share_id_revision_id_app_id_publishe~",
                        columns: x => new { x.share_id, x.revision_id, x.app_id, x.publisher_organization_id, x.organization_id },
                        principalTable: "app_shares",
                        principalColumns: new[] { "id", "revision_id", "app_id", "publisher_organization_id", "destination_organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_imports_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_imports_users_imported_by_user_id",
                        column: x => x.imported_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installed_binding = table.Column<string>(type: "jsonb", nullable: true),
                    candidate = table.Column<string>(type: "jsonb", nullable: true),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_installations", x => x.id);
                    table.UniqueConstraint("AK_app_installations_id_app_id", x => new { x.id, x.app_id });
                    table.UniqueConstraint("AK_app_installations_id_robot_id_organization_id", x => new { x.id, x.robot_id, x.organization_id });
                    table.CheckConstraint("ck_app_installation_json_objects", "(installed_binding IS NULL OR jsonb_typeof(installed_binding) = 'object') AND (candidate IS NULL OR jsonb_typeof(candidate) = 'object')");
                    table.ForeignKey(
                        name: "FK_app_installations_app_imports_import_id_app_id_organization~",
                        columns: x => new { x.import_id, x.app_id, x.organization_id },
                        principalTable: "app_imports",
                        principalColumns: new[] { "id", "app_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_installations_app_revisions_available_revision_id_app_id",
                        columns: x => new { x.available_revision_id, x.app_id },
                        principalTable: "app_revisions",
                        principalColumns: new[] { "id", "app_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_installations_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_installations_robots_robot_id_organization_id",
                        columns: x => new { x.robot_id, x.organization_id },
                        principalTable: "robots",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_installations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_configuration_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_configuration_revisions", x => x.id);
                    table.CheckConstraint("ck_app_configuration_values_object", "jsonb_typeof(values) = 'object'");
                    table.ForeignKey(
                        name: "FK_app_configuration_revisions_app_installations_installation_~",
                        columns: x => new { x.installation_id, x.app_id },
                        principalTable: "app_installations",
                        principalColumns: new[] { "id", "app_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_configuration_revisions_app_revisions_revision_id_app_id",
                        columns: x => new { x.revision_id, x.app_id },
                        principalTable: "app_revisions",
                        principalColumns: new[] { "id", "app_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_configuration_revisions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_operations", x => x.id);
                    table.CheckConstraint("ck_app_operation_deadline", "deadline_at >= requested_at");
                    table.CheckConstraint("ck_app_operation_failure_object", "failure IS NULL OR jsonb_typeof(failure) = 'object'");
                    table.CheckConstraint("ck_app_operation_state", "state IN ('queued','accepted','running','succeeded','failed','cancelled')");
                    table.ForeignKey(
                        name: "FK_app_operations_app_installations_installation_id_robot_id_o~",
                        columns: x => new { x.installation_id, x.robot_id, x.organization_id },
                        principalTable: "app_installations",
                        principalColumns: new[] { "id", "robot_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_operations_robots_robot_id_organization_id",
                        columns: x => new { x.robot_id, x.organization_id },
                        principalTable: "robots",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_operations_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    robot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    failure = table.Column<string>(type: "jsonb", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_runs", x => x.id);
                    table.UniqueConstraint("AK_app_runs_id_installation_id_robot_id_organization_id", x => new { x.id, x.installation_id, x.robot_id, x.organization_id });
                    table.CheckConstraint("ck_app_run_completion", "(state IN ('accepted','running') AND completed_at IS NULL) OR (state IN ('succeeded','failed','stopped','interrupted') AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_app_run_failure_object", "failure IS NULL OR jsonb_typeof(failure) = 'object'");
                    table.CheckConstraint("ck_app_run_snapshot_object", "jsonb_typeof(snapshot) = 'object'");
                    table.CheckConstraint("ck_app_run_state", "state IN ('accepted','running','succeeded','failed','stopped','interrupted')");
                    table.ForeignKey(
                        name: "FK_app_runs_app_installations_installation_id_robot_id_organiz~",
                        columns: x => new { x.installation_id, x.robot_id, x.organization_id },
                        principalTable: "app_installations",
                        principalColumns: new[] { "id", "robot_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_runs_app_operations_start_operation_id",
                        column: x => x.start_operation_id,
                        principalTable: "app_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_runs_robots_robot_id_organization_id",
                        columns: x => new { x.robot_id, x.organization_id },
                        principalTable: "robots",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_runs_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_configuration_revisions_created_by_user_id",
                table: "app_configuration_revisions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_configuration_revisions_installation_id_app_id",
                table: "app_configuration_revisions",
                columns: new[] { "installation_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_configuration_revisions_revision_id_app_id",
                table: "app_configuration_revisions",
                columns: new[] { "revision_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_idempotency_records_principal_user_id_route_target_key",
                table: "app_idempotency_records",
                columns: new[] { "principal_user_id", "route", "target", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_imports_imported_by_user_id",
                table: "app_imports",
                column: "imported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_imports_organization_id_revision_id",
                table: "app_imports",
                columns: new[] { "organization_id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_imports_revision_id_app_id_publisher_organization_id",
                table: "app_imports",
                columns: new[] { "revision_id", "app_id", "publisher_organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_imports_share_id_revision_id_app_id_publisher_organizat~",
                table: "app_imports",
                columns: new[] { "share_id", "revision_id", "app_id", "publisher_organization_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_app_id",
                table: "app_installations",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_available_revision_id_app_id",
                table: "app_installations",
                columns: new[] { "available_revision_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_created_by_user_id",
                table: "app_installations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_import_id_app_id_organization_id",
                table: "app_installations",
                columns: new[] { "import_id", "app_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_robot_id_app_id",
                table: "app_installations",
                columns: new[] { "robot_id", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_robot_id_organization_id",
                table: "app_installations",
                columns: new[] { "robot_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_installation_id_robot_id_organization_id",
                table: "app_operations",
                columns: new[] { "installation_id", "robot_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_installation_id_state",
                table: "app_operations",
                columns: new[] { "installation_id", "state" },
                filter: "state IN ('queued','accepted','running')");

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_requested_by_user_id",
                table: "app_operations",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_robot_id_organization_id",
                table: "app_operations",
                columns: new[] { "robot_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_robot_id_requested_at_id",
                table: "app_operations",
                columns: new[] { "robot_id", "requested_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_operations_run_id_installation_id_robot_id_organization~",
                table: "app_operations",
                columns: new[] { "run_id", "installation_id", "robot_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_revisions_app_id_publisher_organization_id",
                table: "app_revisions",
                columns: new[] { "app_id", "publisher_organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_revisions_app_id_release",
                table: "app_revisions",
                columns: new[] { "app_id", "release" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_revisions_published_by_user_id",
                table: "app_revisions",
                column: "published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_installation_id_robot_id_organization_id",
                table: "app_runs",
                columns: new[] { "installation_id", "robot_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_requested_by_user_id",
                table: "app_runs",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_robot_id_organization_id",
                table: "app_runs",
                columns: new[] { "robot_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_robot_id_requested_at_id",
                table: "app_runs",
                columns: new[] { "robot_id", "requested_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_start_operation_id",
                table: "app_runs",
                column: "start_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_shares_destination_organization_id",
                table: "app_shares",
                column: "destination_organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_shares_granted_by_user_id",
                table: "app_shares",
                column: "granted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_shares_revision_id_app_id_publisher_organization_id",
                table: "app_shares",
                columns: new[] { "revision_id", "app_id", "publisher_organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_apps_created_by_user_id",
                table: "apps",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_apps_publisher_organization_id_app_key",
                table: "apps",
                columns: new[] { "publisher_organization_id", "app_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_app_operations_app_runs_run_id_installation_id_robot_id_org~",
                table: "app_operations",
                columns: new[] { "run_id", "installation_id", "robot_id", "organization_id" },
                principalTable: "app_runs",
                principalColumns: new[] { "id", "installation_id", "robot_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                INSERT INTO robot_execution_slots (robot_id, source, execution_id, claimed_at)
                SELECT robot_id, 'legacy', id, now()
                FROM script_executions
                WHERE state IN ('queued', 'preparing', 'running');

                CREATE FUNCTION maintain_robot_execution_slot() RETURNS trigger AS $$
                DECLARE
                    slot_robot uuid;
                    slot_execution uuid;
                    slot_source text;
                    active boolean;
                BEGIN
                    IF TG_TABLE_NAME = 'script_executions' THEN
                        slot_robot := NEW.robot_id;
                        slot_execution := NEW.id;
                        slot_source := 'legacy';
                        active := NEW.state IN ('queued', 'preparing', 'running');
                    ELSE
                        slot_robot := NEW.robot_id;
                        slot_execution := NEW.id;
                        slot_source := 'app';
                        active := NEW.state IN ('accepted', 'running');
                    END IF;
                    IF active THEN
                        INSERT INTO robot_execution_slots (robot_id, source, execution_id, claimed_at)
                        VALUES (slot_robot, slot_source, slot_execution, now())
                        ON CONFLICT (robot_id) DO NOTHING;
                        IF NOT EXISTS (
                            SELECT 1 FROM robot_execution_slots
                            WHERE robot_id = slot_robot AND source = slot_source AND execution_id = slot_execution
                        ) THEN
                            RAISE EXCEPTION 'robot already has an active execution' USING ERRCODE = '23505';
                        END IF;
                    ELSE
                        DELETE FROM robot_execution_slots
                        WHERE robot_id = slot_robot AND source = slot_source AND execution_id = slot_execution;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_script_execution_slot
                AFTER INSERT OR UPDATE OF robot_id, state ON script_executions
                FOR EACH ROW EXECUTE FUNCTION maintain_robot_execution_slot();
                CREATE TRIGGER trg_app_run_slot
                AFTER INSERT OR UPDATE OF robot_id, state ON app_runs
                FOR EACH ROW EXECUTE FUNCTION maintain_robot_execution_slot();

                CREATE FUNCTION reject_immutable_app_row() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'App provenance records are immutable' USING ERRCODE = '55000';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_app_revision_immutable
                BEFORE UPDATE OR DELETE ON app_revisions
                FOR EACH ROW EXECUTE FUNCTION reject_immutable_app_row();
                CREATE TRIGGER trg_app_import_immutable
                BEFORE UPDATE OR DELETE ON app_imports
                FOR EACH ROW EXECUTE FUNCTION reject_immutable_app_row();
                CREATE TRIGGER trg_app_configuration_immutable
                BEFORE UPDATE OR DELETE ON app_configuration_revisions
                FOR EACH ROW EXECUTE FUNCTION reject_immutable_app_row();
                CREATE TRIGGER trg_app_idempotency_immutable
                BEFORE UPDATE OR DELETE ON app_idempotency_records
                FOR EACH ROW EXECUTE FUNCTION reject_immutable_app_row();

                CREATE FUNCTION reject_app_run_snapshot_change() RETURNS trigger AS $$
                BEGIN
                    IF ROW(NEW.organization_id, NEW.robot_id, NEW.installation_id, NEW.start_operation_id, NEW.snapshot, NEW.requested_by_user_id, NEW.requested_at)
                       IS DISTINCT FROM ROW(OLD.organization_id, OLD.robot_id, OLD.installation_id, OLD.start_operation_id, OLD.snapshot, OLD.requested_by_user_id, OLD.requested_at) THEN
                        RAISE EXCEPTION 'App run attribution is immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_app_run_snapshot_immutable
                BEFORE UPDATE ON app_runs
                FOR EACH ROW EXECUTE FUNCTION reject_app_run_snapshot_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_app_run_slot ON app_runs;
                DROP TRIGGER IF EXISTS trg_script_execution_slot ON script_executions;
                DROP FUNCTION IF EXISTS maintain_robot_execution_slot();
                DROP TRIGGER IF EXISTS trg_app_revision_immutable ON app_revisions;
                DROP TRIGGER IF EXISTS trg_app_import_immutable ON app_imports;
                DROP TRIGGER IF EXISTS trg_app_configuration_immutable ON app_configuration_revisions;
                DROP TRIGGER IF EXISTS trg_app_idempotency_immutable ON app_idempotency_records;
                DROP FUNCTION IF EXISTS reject_immutable_app_row();
                DROP TRIGGER IF EXISTS trg_app_run_snapshot_immutable ON app_runs;
                DROP FUNCTION IF EXISTS reject_app_run_snapshot_change();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_app_operations_app_installations_installation_id_robot_id_o~",
                table: "app_operations");

            migrationBuilder.DropForeignKey(
                name: "FK_app_runs_app_installations_installation_id_robot_id_organiz~",
                table: "app_runs");

            migrationBuilder.DropForeignKey(
                name: "FK_app_operations_app_runs_run_id_installation_id_robot_id_org~",
                table: "app_operations");

            migrationBuilder.DropTable(
                name: "app_configuration_revisions");

            migrationBuilder.DropTable(
                name: "app_idempotency_records");

            migrationBuilder.DropTable(
                name: "robot_execution_slots");

            migrationBuilder.DropTable(
                name: "app_installations");

            migrationBuilder.DropTable(
                name: "app_imports");

            migrationBuilder.DropTable(
                name: "app_shares");

            migrationBuilder.DropTable(
                name: "app_revisions");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "app_runs");

            migrationBuilder.DropTable(
                name: "app_operations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_robots_id_organization_id",
                table: "robots");
        }
    }
}
