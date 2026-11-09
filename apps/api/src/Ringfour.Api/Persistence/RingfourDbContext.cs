using Microsoft.EntityFrameworkCore;

namespace Ringfour.Api.Persistence;

public sealed class RingfourDbContext(DbContextOptions<RingfourDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();

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
    }
}
