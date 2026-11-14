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
    public DbSet<DevicePairingRequest> DevicePairingRequests => Set<DevicePairingRequest>();
    public DbSet<RobotAuditEvent> RobotAuditEvents => Set<RobotAuditEvent>();
    public DbSet<RobotPresence> RobotPresences => Set<RobotPresence>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationIdempotencyRecord> OperationIdempotencyRecords => Set<OperationIdempotencyRecord>();
    public DbSet<ScriptRevision> ScriptRevisions => Set<ScriptRevision>();
    public DbSet<ScriptExecution> ScriptExecutions => Set<ScriptExecution>();
    public DbSet<ScriptArtifact> ScriptArtifacts => Set<ScriptArtifact>();
    public DbSet<ScriptExecutionEvent> ScriptExecutionEvents => Set<ScriptExecutionEvent>();
    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<MissionRevision> MissionRevisions => Set<MissionRevision>();
    public DbSet<MissionDeployment> MissionDeployments => Set<MissionDeployment>();
    public DbSet<DeploymentAction> DeploymentActions => Set<DeploymentAction>();
    public DbSet<DevSession> DevSessions => Set<DevSession>();
    public DbSet<DevSessionGeneration> DevSessionGenerations => Set<DevSessionGeneration>();
    public DbSet<DevLocalEndpoint> DevLocalEndpoints => Set<DevLocalEndpoint>();
    public DbSet<DevLocalGrant> DevLocalGrants => Set<DevLocalGrant>();
    public DbSet<DevSessionRun> DevSessionRuns => Set<DevSessionRun>();
    public DbSet<LogbookEvent> LogbookEvents => Set<LogbookEvent>();
    public DbSet<LogbookCheckpoint> LogbookCheckpoints => Set<LogbookCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LogbookEvent>(entity =>
        {
            entity.ToTable("logbook_events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.OperationId).HasColumnName("operation_id");
            entity.Property(item => item.LocalSequence).HasColumnName("local_sequence");
            entity.Property(item => item.Type).HasColumnName("type");
            entity.Property(item => item.SchemaVersion).HasColumnName("schema_version");
            entity.Property(item => item.OccurredAt).HasColumnName("occurred_at");
            entity.Property(item => item.ReceivedAt).HasColumnName("received_at");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.ExecutionId).HasColumnName("execution_id");
            entity.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.HasIndex(item => new { item.RobotId, item.LocalSequence }).IsUnique();
            entity.HasIndex(item => new { item.RobotId, item.ReceivedAt, item.Id });
            entity.HasOne(item => item.Robot).WithMany().HasForeignKey(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Operation).WithMany().HasForeignKey(item => item.OperationId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_sequence", "local_sequence BETWEEN 1 AND 9007199254740991"));
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_state", "state IN ('accepted','running','succeeded','failed','cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_version", "schema_version = 1 AND ((type = 'device.operation.state_changed' AND operation_id IS NOT NULL AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND operation_id IS NOT NULL AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL))"));
        });
        modelBuilder.Entity<LogbookCheckpoint>(entity =>
        {
            entity.ToTable("logbook_checkpoints");
            entity.HasKey(item => item.RobotId);
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.Sequence).HasColumnName("sequence");
            entity.HasOne(item => item.Robot).WithOne().HasForeignKey<LogbookCheckpoint>(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_checkpoint", "sequence BETWEEN 0 AND 9007199254740991"));
        });
        modelBuilder.Entity<DevSessionRun>(entity =>
        {
            entity.ToTable("dev_session_runs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.SessionId).HasColumnName("session_id");
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(item => item.OperationId).HasColumnName("operation_id");
            entity.Property(item => item.Generation).HasColumnName("generation");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.ExitCode).HasColumnName("exit_code");
            entity.Property(item => item.Error).HasColumnName("error").HasColumnType("jsonb");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(item => item.OperationId).IsUnique();
            entity.HasIndex(item => new { item.RobotId, item.State });
            entity.HasOne(item => item.Session).WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Robot).WithMany().HasForeignKey(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Actor).WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Operation).WithOne().HasForeignKey<DevSessionRun>(item => item.OperationId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_run_state", "state IN ('queued','preparing','running','succeeded','failed','cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_run_generation", "generation BETWEEN 1 AND 20"));
        });
        modelBuilder.Entity<DevLocalEndpoint>(entity =>
        {
            entity.ToTable("dev_local_endpoints");
            entity.HasKey(item => item.RobotId);
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.CredentialId).HasColumnName("credential_id");
            entity.Property(item => item.CertificateSha256).HasColumnName("certificate_sha256");
            entity.Property(item => item.DaemonUrl).HasColumnName("daemon_url");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
            entity.HasOne(item => item.Robot).WithOne().HasForeignKey<DevLocalEndpoint>(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Credential).WithMany().HasForeignKey(item => item.CredentialId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_endpoint_fingerprint", "certificate_sha256 ~ '^[0-9a-f]{64}$'"));
        });
        modelBuilder.Entity<DevLocalGrant>(entity =>
        {
            entity.ToTable("dev_local_grants");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.SessionId).HasColumnName("session_id");
            entity.Property(item => item.OperationId).HasColumnName("operation_id");
            entity.Property(item => item.CertificateSha256).HasColumnName("certificate_sha256");
            entity.Property(item => item.DaemonUrl).HasColumnName("daemon_url");
            entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            entity.HasIndex(item => item.OperationId).IsUnique();
            entity.HasOne(item => item.Session).WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Operation).WithOne().HasForeignKey<DevLocalGrant>(item => item.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DevSession>(entity =>
        {
            entity.ToTable("dev_sessions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.Generation).HasColumnName("generation");
            entity.Property(item => item.ActiveGeneration).HasColumnName("active_generation");
            entity.Property(item => item.DependencyDigest).HasColumnName("dependency_digest");
            entity.Property(item => item.LastOperationId).HasColumnName("last_operation_id");
            entity.Property(item => item.LastExecutionId).HasColumnName("last_execution_id");
            entity.Property(item => item.LastError).HasColumnName("last_error").HasColumnType("jsonb");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
            entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            entity.Property(item => item.ClosedAt).HasColumnName("closed_at");
            entity.HasOne(item => item.Robot).WithMany().HasForeignKey(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Owner).WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => item.RobotId).IsUnique().HasFilter("state NOT IN ('closed', 'expired')");
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_session_state", "state IN ('creating', 'ready', 'syncing', 'starting', 'running', 'stopping', 'closing', 'closed', 'expired', 'failed')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_session_generations", "generation >= 0 AND (active_generation IS NULL OR (active_generation > 0 AND active_generation <= generation))"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_session_expiry", "expires_at > created_at"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_session_closed", "(state IN ('closed', 'expired')) = (closed_at IS NOT NULL)"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_session_digest", "dependency_digest IS NULL OR dependency_digest ~ '^[0-9a-f]{64}$'"));
        });
        modelBuilder.Entity<DevSessionGeneration>(entity =>
        {
            entity.ToTable("dev_session_generations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.SessionId).HasColumnName("session_id");
            entity.Property(item => item.OperationId).HasColumnName("operation_id");
            entity.Property(item => item.Generation).HasColumnName("generation");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.ArchiveSha256).HasColumnName("archive_sha256");
            entity.Property(item => item.ArchiveSize).HasColumnName("archive_size");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.HasOne(item => item.Session).WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Operation).WithMany().HasForeignKey(item => item.OperationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => item.OperationId).IsUnique();
            entity.HasIndex(item => new { item.SessionId, item.Generation }).IsUnique().HasFilter("state IN ('pending', 'committed')");
            entity.HasIndex(item => item.SessionId).IsUnique().HasFilter("state = 'pending'");
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_generation_state", "state IN ('pending', 'committed', 'failed')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_generation_number", "generation > 0"));
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_generation_archive", "archive_size BETWEEN 1 AND 1048576 AND archive_sha256 ~ '^[0-9a-f]{64}$'"));
        });
        modelBuilder.Entity<MissionDeployment>(entity =>
        {
            entity.ToTable("mission_deployments");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.SelectedRevisionId).HasColumnName("selected_revision_id");
            entity.Property(item => item.CandidateRevisionId).HasColumnName("candidate_revision_id");
            entity.Property(item => item.CandidateOperationId).HasColumnName("candidate_operation_id");
            entity.Property(item => item.DesiredState).HasColumnName("desired_state");
            entity.Property(item => item.ObservedState).HasColumnName("observed_state");
            entity.Property(item => item.LastOperationId).HasColumnName("last_operation_id");
            entity.Property(item => item.LastExecutionId).HasColumnName("last_execution_id");
            entity.Property(item => item.LastError).HasColumnName("last_error").HasColumnType("jsonb");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(item => item.RobotId).IsUnique();
            entity.HasOne(item => item.Robot).WithMany().HasForeignKey(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.SelectedRevision).WithMany().HasForeignKey(item => item.SelectedRevisionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.CandidateRevision).WithMany().HasForeignKey(item => item.CandidateRevisionId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_mission_desired", "desired_state IN ('stopped', 'running')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_mission_observed", "observed_state IN ('preparing', 'ready', 'starting', 'running', 'stopping', 'succeeded', 'failed', 'cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_mission_candidate", "(candidate_revision_id IS NULL) = (candidate_operation_id IS NULL)"));
        });
        modelBuilder.Entity<DeploymentAction>(entity =>
        {
            entity.ToTable("deployment_actions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.DeploymentId).HasColumnName("deployment_id");
            entity.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(item => item.Kind).HasColumnName("kind");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint");
            entity.Property(item => item.ExecutionId).HasColumnName("execution_id");
            entity.Property(item => item.TargetExecutionId).HasColumnName("target_execution_id");
            entity.Property(item => item.StopOperationId).HasColumnName("stop_operation_id");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
            entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            entity.HasIndex(item => new { item.DeploymentId, item.ActorUserId, item.Kind, item.IdempotencyKey }).IsUnique();
            entity.HasOne(item => item.Deployment).WithMany().HasForeignKey(item => item.DeploymentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Actor).WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Operation).WithOne().HasForeignKey<DeploymentAction>(item => item.Id).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_deployment_action_state", "state IN ('pending', 'starting', 'completed', 'failed')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_deployment_action_kind", "kind IN ('start', 'stop', 'restart')"));
        });
        modelBuilder.Entity<Mission>(entity =>
        {
            entity.ToTable("missions");
            entity.HasKey(mission => mission.Id);
            entity.Property(mission => mission.Id).HasColumnName("id");
            entity.Property(mission => mission.OrganizationId).HasColumnName("organization_id");
            entity.Property(mission => mission.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(mission => mission.Name).HasColumnName("name");
            entity.Property(mission => mission.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(mission => mission.RequestFingerprint).HasColumnName("request_fingerprint");
            entity.Property(mission => mission.LatestVersion).HasColumnName("latest_version");
            entity.Property(mission => mission.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(mission => new { mission.OrganizationId, mission.Name }).IsUnique();
            entity.HasIndex(mission => new { mission.CreatedByUserId, mission.OrganizationId, mission.IdempotencyKey }).IsUnique();
            entity.HasOne(mission => mission.Organization).WithMany().HasForeignKey(mission => mission.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(mission => mission.CreatedBy).WithMany().HasForeignKey(mission => mission.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_mission_version", "latest_version >= 0"));
        });
        modelBuilder.Entity<MissionRevision>(entity =>
        {
            entity.ToTable("mission_revisions");
            entity.HasKey(revision => revision.Id);
            entity.Property(revision => revision.Id).HasColumnName("id");
            entity.Property(revision => revision.MissionId).HasColumnName("mission_id");
            entity.Property(revision => revision.ScriptRevisionId).HasColumnName("script_revision_id");
            entity.Property(revision => revision.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(revision => revision.Version).HasColumnName("version");
            entity.Property(revision => revision.Manifest).HasColumnName("manifest").HasColumnType("jsonb");
            entity.Property(revision => revision.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(revision => revision.RequestFingerprint).HasColumnName("request_fingerprint");
            entity.Property(revision => revision.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(revision => new { revision.MissionId, revision.Version }).IsUnique();
            entity.HasIndex(revision => new { revision.MissionId, revision.CreatedByUserId, revision.IdempotencyKey }).IsUnique();
            entity.HasOne(revision => revision.Mission).WithMany().HasForeignKey(revision => revision.MissionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(revision => revision.ScriptRevision).WithMany().HasForeignKey(revision => revision.ScriptRevisionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(revision => revision.CreatedBy).WithMany().HasForeignKey(revision => revision.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_mission_revision_version", "version > 0"));
        });
        modelBuilder.Entity<ScriptExecutionEvent>(entity =>
        {
            entity.ToTable("script_execution_events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.ExecutionId).HasColumnName("execution_id");
            entity.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.OccurredAt).HasColumnName("occurred_at");
            entity.HasIndex(item => new { item.ExecutionId, item.State }).IsUnique();
            entity.HasOne(item => item.Execution).WithMany().HasForeignKey(item => item.ExecutionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Actor).WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ScriptExecution>(entity =>
        {
            entity.ToTable("script_executions");
            entity.HasKey(execution => execution.Id);
            entity.Property(execution => execution.Id).HasColumnName("id");
            entity.Property(execution => execution.RobotId).HasColumnName("robot_id");
            entity.Property(execution => execution.RevisionId).HasColumnName("revision_id");
            entity.Property(execution => execution.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(execution => execution.DeploymentId).HasColumnName("deployment_id");
            entity.Property(execution => execution.MissionRevisionId).HasColumnName("mission_revision_id");
            entity.Property(execution => execution.State).HasColumnName("state");
            entity.Property(execution => execution.CreatedAt).HasColumnName("created_at");
            entity.Property(execution => execution.UpdatedAt).HasColumnName("updated_at");
            entity.Property(execution => execution.ExitCode).HasColumnName("exit_code");
            entity.Property(execution => execution.Error).HasColumnName("error").HasColumnType("jsonb");
            entity.Property(execution => execution.Stdout).HasColumnName("stdout");
            entity.Property(execution => execution.Stderr).HasColumnName("stderr");
            entity.Property(execution => execution.StdoutTruncated).HasColumnName("stdout_truncated");
            entity.Property(execution => execution.StderrTruncated).HasColumnName("stderr_truncated");
            entity.HasIndex(execution => execution.RobotId).IsUnique().HasFilter("state IN ('queued', 'preparing', 'running')");
            entity.HasOne(execution => execution.Robot).WithMany().HasForeignKey(execution => execution.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(execution => execution.Revision).WithMany().HasForeignKey(execution => execution.RevisionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(execution => execution.Actor).WithMany().HasForeignKey(execution => execution.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(execution => execution.Deployment).WithMany().HasForeignKey(execution => execution.DeploymentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(execution => execution.MissionRevision).WithMany().HasForeignKey(execution => execution.MissionRevisionId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_execution_mission_attribution", "(deployment_id IS NULL) = (mission_revision_id IS NULL)"));
            entity.HasOne(execution => execution.Operation).WithOne().HasForeignKey<ScriptExecution>(execution => execution.Id).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_script_execution_state", "state IN ('queued', 'preparing', 'running', 'succeeded', 'failed', 'cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_script_execution_output", "octet_length(stdout) <= 65536 AND octet_length(stderr) <= 65536"));
        });
        modelBuilder.Entity<ScriptArtifact>(entity =>
        {
            entity.ToTable("script_artifacts");
            entity.HasKey(artifact => new { artifact.ExecutionId, artifact.Path });
            entity.Property(artifact => artifact.ExecutionId).HasColumnName("execution_id");
            entity.Property(artifact => artifact.Path).HasColumnName("path");
            entity.Property(artifact => artifact.Size).HasColumnName("size");
            entity.Property(artifact => artifact.Sha256).HasColumnName("sha256");
            entity.HasOne(artifact => artifact.Execution).WithMany().HasForeignKey(artifact => artifact.ExecutionId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_script_artifact_size", "size BETWEEN 0 AND 262144"));
        });
        modelBuilder.Entity<ScriptRevision>(entity =>
        {
            entity.ToTable("script_revisions");
            entity.HasKey(revision => revision.Id);
            entity.Property(revision => revision.Id).HasColumnName("id");
            entity.Property(revision => revision.OrganizationId).HasColumnName("organization_id");
            entity.Property(revision => revision.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(revision => revision.State).HasColumnName("state");
            entity.Property(revision => revision.ArchiveSha256).HasColumnName("archive_sha256");
            entity.Property(revision => revision.ArchiveSize).HasColumnName("archive_size");
            entity.Property(revision => revision.RevisionDigest).HasColumnName("revision_digest");
            entity.Property(revision => revision.Entrypoint).HasColumnName("entrypoint");
            entity.Property(revision => revision.PythonRequirement).HasColumnName("python_requirement");
            entity.Property(revision => revision.LockSha256).HasColumnName("lock_sha256");
            entity.Property(revision => revision.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(revision => revision.RequestFingerprint).HasColumnName("request_fingerprint");
            entity.Property(revision => revision.CreatedAt).HasColumnName("created_at");
            entity.Property(revision => revision.UploadExpiresAt).HasColumnName("upload_expires_at");
            entity.Property(revision => revision.FinalizedAt).HasColumnName("finalized_at");
            entity.Property(revision => revision.UploadCleanedAt).HasColumnName("upload_cleaned_at");
            entity.HasIndex(revision => new { revision.CreatedByUserId, revision.OrganizationId, revision.IdempotencyKey }).IsUnique();
            entity.HasIndex(revision => revision.OrganizationId);
            entity.HasOne(revision => revision.Organization).WithMany().HasForeignKey(revision => revision.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(revision => revision.CreatedBy).WithMany().HasForeignKey(revision => revision.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_script_revision_state", "(state = 'uploading' AND finalized_at IS NULL) OR (state = 'ready' AND finalized_at IS NOT NULL)"));
            entity.ToTable(table => table.HasCheckConstraint("ck_script_revision_size", "archive_size BETWEEN 1 AND 1048576"));
        });
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

        modelBuilder.Entity<DevicePairingRequest>(entity =>
        {
            entity.ToTable("device_pairing_requests");
            entity.HasKey(pairing => pairing.Id);
            entity.Property(pairing => pairing.Id).HasColumnName("id");
            entity.Property(pairing => pairing.CodeHash).HasColumnName("code_hash");
            entity.Property(pairing => pairing.CredentialHash).HasColumnName("credential_hash");
            entity.Property(pairing => pairing.CreatedAt).HasColumnName("created_at");
            entity.Property(pairing => pairing.ExpiresAt).HasColumnName("expires_at");
            entity.Property(pairing => pairing.ClaimedAt).HasColumnName("claimed_at");
            entity.Property(pairing => pairing.RobotId).HasColumnName("robot_id");
            entity.Property(pairing => pairing.CredentialId).HasColumnName("credential_id");
            entity.HasIndex(pairing => pairing.CodeHash).IsUnique();
            entity.HasIndex(pairing => pairing.CredentialHash).IsUnique();
            entity.HasIndex(pairing => pairing.ExpiresAt);
            entity.HasOne(pairing => pairing.Robot).WithMany().HasForeignKey(pairing => pairing.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(pairing => pairing.Credential).WithMany().HasForeignKey(pairing => pairing.CredentialId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_pairing_claim", "(claimed_at IS NULL AND robot_id IS NULL AND credential_id IS NULL) OR (claimed_at IS NOT NULL AND robot_id IS NOT NULL AND credential_id IS NOT NULL)"));
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

        modelBuilder.Entity<RobotPresence>(entity =>
        {
            entity.ToTable("robot_presence");
            entity.HasKey(presence => presence.RobotId);
            entity.Property(presence => presence.RobotId).HasColumnName("robot_id");
            entity.Property(presence => presence.SessionId).HasColumnName("session_id");
            entity.Property(presence => presence.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(presence => presence.DaemonVersion).HasColumnName("daemon_version");
            entity.Property(presence => presence.ProtocolVersion).HasColumnName("protocol_version");
            entity.HasOne(presence => presence.Robot)
                .WithMany()
                .HasForeignKey(presence => presence.RobotId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Operation>(entity =>
        {
            entity.ToTable("operations");
            entity.HasKey(operation => operation.Id);
            entity.Property(operation => operation.Id).HasColumnName("id");
            entity.Property(operation => operation.RobotId).HasColumnName("robot_id");
            entity.Property(operation => operation.Kind).HasColumnName("kind");
            entity.Property(operation => operation.PayloadVersion).HasColumnName("payload_version");
            entity.Property(operation => operation.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb");
            entity.Property(operation => operation.State)
                .HasColumnName("state")
                .HasConversion<string>();
            entity.Property(operation => operation.CreatedAt).HasColumnName("created_at");
            entity.Property(operation => operation.UpdatedAt).HasColumnName("updated_at");
            entity.Property(operation => operation.DeadlineAt).HasColumnName("deadline_at");
            entity.Property(operation => operation.RequestedByUserId).HasColumnName("requested_by_user_id");
            entity.Property(operation => operation.AcknowledgedAt).HasColumnName("acknowledged_at");
            entity.Property(operation => operation.Result)
                .HasColumnName("result")
                .HasColumnType("jsonb");
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_operations_state",
                "state IN ('Queued', 'Accepted', 'Running', 'Succeeded', 'Failed', 'Cancelled')"));
            entity.HasIndex(operation => new { operation.RobotId, operation.State, operation.CreatedAt, operation.Id });
            entity.HasOne(operation => operation.Robot)
                .WithMany()
                .HasForeignKey(operation => operation.RobotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(operation => operation.RequestedBy)
                .WithMany()
                .HasForeignKey(operation => operation.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OperationIdempotencyRecord>(entity =>
        {
            entity.ToTable("operation_idempotency_records");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).HasColumnName("id");
            entity.Property(record => record.PrincipalUserId).HasColumnName("principal_user_id");
            entity.Property(record => record.Route).HasColumnName("route");
            entity.Property(record => record.Target).HasColumnName("target");
            entity.Property(record => record.Key).HasColumnName("key");
            entity.Property(record => record.RequestFingerprint).HasColumnName("request_fingerprint");
            entity.Property(record => record.OutcomeOperationId).HasColumnName("outcome_operation_id");
            entity.Property(record => record.CreatedAt).HasColumnName("created_at");
            entity.Property(record => record.ExpiresAt).HasColumnName("expires_at");
            entity.HasIndex(record =>
                    new { record.PrincipalUserId, record.Route, record.Target, record.Key })
                .IsUnique();
            entity.HasOne(record => record.Principal)
                .WithMany()
                .HasForeignKey(record => record.PrincipalUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(record => record.Outcome)
                .WithMany()
                .HasForeignKey(record => record.OutcomeOperationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
