using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Development;

public sealed class AppDevelopmentSession
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid OwnerUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int Generation { get; set; }
    public Guid? SourceOperationId { get; set; }
    public string? ArchiveSha256 { get; set; }
    public string? GrantHash { get; set; }
    public DateTimeOffset? GrantExpiresAt { get; set; }
}
public sealed class AppDevelopmentEndpoint
{
    public Guid RobotId { get; set; }
    public Guid CredentialId { get; set; }
    public string DaemonUrl { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
public static class DevelopmentEntities
{
    public static void ConfigureAppDevelopment(this ModelBuilder model)
    {
        model.Entity<AppDevelopmentSession>(e =>
        {
            e.ToTable("app_development_sessions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RobotId).HasColumnName("robot_id");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.SourceOperationId).HasColumnName("source_operation_id");
            e.Property(x => x.ArchiveSha256).HasColumnName("archive_sha256").HasMaxLength(71);
            e.Property(x => x.GrantHash).HasColumnName("grant_hash").HasMaxLength(64);
            e.Property(x => x.GrantExpiresAt).HasColumnName("grant_expires_at");
            e.HasIndex(x => x.RobotId).IsUnique().HasFilter("closed_at IS NULL");
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppInstallation>().WithMany().HasForeignKey(x => x.InstallationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_development_generation", "generation >= 0"));
        });
        model.Entity<AppDevelopmentEndpoint>(e =>
        {
            e.ToTable("app_development_endpoints"); e.HasKey(x => x.RobotId);
            e.Property(x => x.RobotId).HasColumnName("robot_id");
            e.Property(x => x.CredentialId).HasColumnName("credential_id");
            e.Property(x => x.DaemonUrl).HasColumnName("daemon_url").HasMaxLength(512);
            e.Property(x => x.CertificateSha256).HasColumnName("certificate_sha256").HasMaxLength(64);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RobotCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
