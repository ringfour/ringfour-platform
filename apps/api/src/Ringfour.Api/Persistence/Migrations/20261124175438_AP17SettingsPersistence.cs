using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AP17SettingsPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "applied_settings_revision_id",
                table: "app_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "selected_settings_revision_id",
                table: "app_installations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_app_runs_id_installation_id",
                table: "app_runs",
                columns: new[] { "id", "installation_id" });

            migrationBuilder.CreateTable(
                name: "app_settings_revisions",
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
                    table.PrimaryKey("PK_app_settings_revisions", x => x.id);
                    table.UniqueConstraint("AK_app_settings_revisions_id_installation_id", x => new { x.id, x.installation_id });
                    table.CheckConstraint("ck_app_settings_values_object", "jsonb_typeof(values) = 'object' AND octet_length(values::text) <= 16384");
                    table.ForeignKey(
                        name: "FK_app_settings_revisions_app_installations_installation_id_ap~",
                        columns: x => new { x.installation_id, x.app_id },
                        principalTable: "app_installations",
                        principalColumns: new[] { "id", "app_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_revisions_app_revisions_revision_id_app_id",
                        columns: x => new { x.revision_id, x.app_id },
                        principalTable: "app_revisions",
                        principalColumns: new[] { "id", "app_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_revisions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_settings_applications",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_applied_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_selected_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initial = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    delivery_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locally_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings_applications", x => x.application_id);
                    table.CheckConstraint("ck_app_settings_application_delivery", "delivery_state IN ('queued','delivered','locally_received','reported')");
                    table.CheckConstraint("ck_app_settings_application_outcome", "(state = 'pending' AND outcome IS NULL AND completed_at IS NULL) OR (state = 'applied' AND outcome = 'applied' AND reason IS NULL AND completed_at IS NOT NULL) OR (state = 'rejected' AND outcome = 'rejected' AND completed_at IS NOT NULL) OR (state = 'ended_before_confirmation' AND outcome IS NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_app_settings_application_reason", "reason IS NULL OR (length(reason) <= 512 AND reason !~ '[[:cntrl:]]')");
                    table.CheckConstraint("ck_app_settings_application_state", "state IN ('pending','applied','rejected','ended_before_confirmation')");
                    table.ForeignKey(
                        name: "FK_app_settings_applications_app_operations_application_id",
                        column: x => x.application_id,
                        principalTable: "app_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_applications_app_runs_run_id_installation_id",
                        columns: x => new { x.run_id, x.installation_id },
                        principalTable: "app_runs",
                        principalColumns: new[] { "id", "installation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_applications_app_settings_revisions_expected_a~",
                        columns: x => new { x.expected_applied_revision_id, x.installation_id },
                        principalTable: "app_settings_revisions",
                        principalColumns: new[] { "id", "installation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_applications_app_settings_revisions_expected_s~",
                        columns: x => new { x.expected_selected_revision_id, x.installation_id },
                        principalTable: "app_settings_revisions",
                        principalColumns: new[] { "id", "installation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_applications_app_settings_revisions_settings_r~",
                        columns: x => new { x.settings_revision_id, x.installation_id },
                        principalTable: "app_settings_revisions",
                        principalColumns: new[] { "id", "installation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_applications_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_runs_applied_settings_revision_id_installation_id",
                table: "app_runs",
                columns: new[] { "applied_settings_revision_id", "installation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_installations_selected_settings_revision_id_id",
                table: "app_installations",
                columns: new[] { "selected_settings_revision_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_expected_applied_revision_id_inst~",
                table: "app_settings_applications",
                columns: new[] { "expected_applied_revision_id", "installation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_expected_selected_revision_id_ins~",
                table: "app_settings_applications",
                columns: new[] { "expected_selected_revision_id", "installation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_installation_id_requested_at_appl~",
                table: "app_settings_applications",
                columns: new[] { "installation_id", "requested_at", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_requested_by_user_id",
                table: "app_settings_applications",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_run_id",
                table: "app_settings_applications",
                column: "run_id",
                unique: true,
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_run_id_installation_id",
                table: "app_settings_applications",
                columns: new[] { "run_id", "installation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_applications_settings_revision_id_installation~",
                table: "app_settings_applications",
                columns: new[] { "settings_revision_id", "installation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_revisions_created_by_user_id",
                table: "app_settings_revisions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_revisions_installation_id_app_id",
                table: "app_settings_revisions",
                columns: new[] { "installation_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_revisions_installation_id_created_at_id",
                table: "app_settings_revisions",
                columns: new[] { "installation_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_revisions_revision_id_app_id",
                table: "app_settings_revisions",
                columns: new[] { "revision_id", "app_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_app_installations_app_settings_revisions_selected_settings_~",
                table: "app_installations",
                columns: new[] { "selected_settings_revision_id", "id" },
                principalTable: "app_settings_revisions",
                principalColumns: new[] { "id", "installation_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_app_runs_app_settings_revisions_applied_settings_revision_i~",
                table: "app_runs",
                columns: new[] { "applied_settings_revision_id", "installation_id" },
                principalTable: "app_settings_revisions",
                principalColumns: new[] { "id", "installation_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_app_installations_app_settings_revisions_selected_settings_~",
                table: "app_installations");

            migrationBuilder.DropForeignKey(
                name: "FK_app_runs_app_settings_revisions_applied_settings_revision_i~",
                table: "app_runs");

            migrationBuilder.DropTable(
                name: "app_settings_applications");

            migrationBuilder.DropTable(
                name: "app_settings_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_app_runs_id_installation_id",
                table: "app_runs");

            migrationBuilder.DropIndex(
                name: "IX_app_runs_applied_settings_revision_id_installation_id",
                table: "app_runs");

            migrationBuilder.DropIndex(
                name: "IX_app_installations_selected_settings_revision_id_id",
                table: "app_installations");

            migrationBuilder.DropColumn(
                name: "applied_settings_revision_id",
                table: "app_runs");

            migrationBuilder.DropColumn(
                name: "selected_settings_revision_id",
                table: "app_installations");
        }
    }
}
