using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScriptRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "script_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    archive_sha256 = table.Column<string>(type: "text", nullable: false),
                    archive_size = table.Column<int>(type: "integer", nullable: false),
                    revision_digest = table.Column<string>(type: "text", nullable: false),
                    entrypoint = table.Column<string>(type: "text", nullable: false),
                    python_requirement = table.Column<string>(type: "text", nullable: false),
                    lock_sha256 = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_revisions", x => x.id);
                    table.CheckConstraint("ck_script_revision_size", "archive_size BETWEEN 1 AND 1048576");
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

            migrationBuilder.CreateIndex(
                name: "IX_script_revisions_created_by_user_id_organization_id_idempot~",
                table: "script_revisions",
                columns: new[] { "created_by_user_id", "organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_script_revisions_organization_id",
                table: "script_revisions",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "script_revisions");
        }
    }
}
