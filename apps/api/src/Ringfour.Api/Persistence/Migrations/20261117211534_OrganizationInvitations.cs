using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ringfour.Api.Persistence.Migrations;

public partial class OrganizationInvitations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organization_invitations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "text", nullable: false),
                normalized_email = table.Column<string>(type: "text", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                token_hash = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organization_invitations", item => item.id);
                table.CheckConstraint("ck_organization_invitation_role", "role IN ('Owner', 'Developer', 'Operator')");
                table.CheckConstraint("ck_organization_invitation_state", "(accepted_at IS NULL AND accepted_by_user_id IS NULL) OR (accepted_at IS NOT NULL AND accepted_by_user_id IS NOT NULL)");
                table.ForeignKey("FK_organization_invitations_organizations_organization_id", item => item.organization_id, "organizations", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_organization_invitations_users_invited_by_user_id", item => item.invited_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_organization_invitations_users_accepted_by_user_id", item => item.accepted_by_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_organization_invitations_accepted_by_user_id", "organization_invitations", "accepted_by_user_id");
        migrationBuilder.CreateIndex("IX_organization_invitations_invited_by_user_id", "organization_invitations", "invited_by_user_id");
        migrationBuilder.CreateIndex(
            "IX_organization_invitations_organization_id_normalized_email",
            "organization_invitations", new[] { "organization_id", "normalized_email" },
            unique: true, filter: "accepted_at IS NULL AND revoked_at IS NULL");
        migrationBuilder.CreateIndex("IX_organization_invitations_token_hash", "organization_invitations", "token_hash", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("organization_invitations");
}
