using Microsoft.EntityFrameworkCore;

namespace Ringfour.Api.Persistence;

public sealed class RingfourDbContext(DbContextOptions<RingfourDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Robot> Robots => Set<Robot>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<RobotCredential> RobotCredentials => Set<RobotCredential>();
    public DbSet<RobotAuditEvent> RobotAuditEvents => Set<RobotAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id");
            entity.Property(user => user.Auth0Sub).HasColumnName("auth0_sub");
            entity.Property(user => user.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(user => user.Auth0Sub).IsUnique();
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.HasKey(organization => organization.Id);
            entity.Property(organization => organization.Id).HasColumnName("id");
            entity.Property(organization => organization.Name).HasColumnName("name");
            entity.Property(organization => organization.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<Membership>(entity =>
        {
            entity.ToTable("memberships");
            entity.HasKey(membership => new { membership.OrganizationId, membership.UserId });
            entity.Property(membership => membership.OrganizationId).HasColumnName("organization_id");
            entity.Property(membership => membership.UserId).HasColumnName("user_id");
            entity.Property(membership => membership.Role)
                .HasColumnName("role")
                .HasConversion<string>();
            entity.Property(membership => membership.CreatedAt).HasColumnName("created_at");
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_memberships_role", "role IN ('Owner', 'Developer', 'Operator')"));
            entity.HasIndex(membership => new { membership.UserId, membership.OrganizationId });
            entity.HasOne(membership => membership.Organization)
                .WithMany(organization => organization.Memberships)
                .HasForeignKey(membership => membership.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(membership => membership.User)
                .WithMany(user => user.Memberships)
                .HasForeignKey(membership => membership.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Robot>(entity =>
        {
            entity.ToTable("robots");
            entity.HasKey(robot => robot.Id);
            entity.Property(robot => robot.Id).HasColumnName("id");
            entity.Property(robot => robot.OrganizationId).HasColumnName("organization_id");
            entity.Property(robot => robot.Name).HasColumnName("name");
            entity.Property(robot => robot.CreatedAt).HasColumnName("created_at");
            entity.Property(robot => robot.EnrolledAt).HasColumnName("enrolled_at");
            entity.Property(robot => robot.ProfileRef).HasColumnName("profile_ref");
            entity.HasIndex(robot => robot.OrganizationId);
            entity.HasOne(robot => robot.Organization)
                .WithMany()
                .HasForeignKey(robot => robot.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EnrollmentToken>(entity =>
        {
            entity.ToTable("enrollment_tokens");
            entity.HasKey(token => token.Id);
            entity.Property(token => token.Id).HasColumnName("id");
            entity.Property(token => token.RobotId).HasColumnName("robot_id");
            entity.Property(token => token.TokenHash).HasColumnName("token_hash");
            entity.Property(token => token.IssuedByUserId).HasColumnName("issued_by_user_id");
            entity.Property(token => token.CreatedAt).HasColumnName("created_at");
            entity.Property(token => token.ExpiresAt).HasColumnName("expires_at");
            entity.Property(token => token.ConsumedAt).HasColumnName("consumed_at");
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => token.RobotId);
            entity.HasOne(token => token.Robot)
                .WithMany(robot => robot.EnrollmentTokens)
                .HasForeignKey(token => token.RobotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(token => token.IssuedBy)
                .WithMany()
                .HasForeignKey(token => token.IssuedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RobotCredential>(entity =>
        {
            entity.ToTable("robot_credentials");
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.Id).HasColumnName("id");
            entity.Property(credential => credential.RobotId).HasColumnName("robot_id");
            entity.Property(credential => credential.SecretHash).HasColumnName("secret_hash");
            entity.Property(credential => credential.CreatedAt).HasColumnName("created_at");
            entity.Property(credential => credential.ExpiresAt).HasColumnName("expires_at");
            entity.Property(credential => credential.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(credential => credential.SecretHash).IsUnique();
            entity.HasIndex(credential => credential.RobotId);
            entity.HasOne(credential => credential.Robot)
                .WithMany(robot => robot.Credentials)
                .HasForeignKey(credential => credential.RobotId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RobotAuditEvent>(entity =>
        {
            entity.ToTable("robot_audit_events");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.Id).HasColumnName("id");
            entity.Property(audit => audit.RobotId).HasColumnName("robot_id");
            entity.Property(audit => audit.OccurredAt).HasColumnName("occurred_at");
            entity.Property(audit => audit.Type).HasColumnName("type");
            entity.Property(audit => audit.ActorUserId).HasColumnName("actor_user_id");
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_robot_audit_events_type",
                "type IN ('robot_created', 'enrollment_token_issued', 'enrolled'," +
                " 'credential_rotated', 'credential_revoked')"));
            entity.HasIndex(audit => new { audit.RobotId, audit.OccurredAt, audit.Id });
            entity.HasOne(audit => audit.Robot)
                .WithMany(robot => robot.AuditEvents)
                .HasForeignKey(audit => audit.RobotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(audit => audit.Actor)
                .WithMany()
                .HasForeignKey(audit => audit.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
