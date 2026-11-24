using Microsoft.EntityFrameworkCore;

namespace Ringfour.Api.Persistence;

public sealed class RingfourDbContext(DbContextOptions<RingfourDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();
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
    public DbSet<HumanInteraction> HumanInteractions => Set<HumanInteraction>();
    public DbSet<HumanInteractionResponse> HumanInteractionResponses => Set<HumanInteractionResponse>();
    public DbSet<MissionUiSession> MissionUiSessions => Set<MissionUiSession>();
    public DbSet<AppRecord> Apps => Set<AppRecord>();
    public DbSet<AppRevision> AppRevisions => Set<AppRevision>();
    public DbSet<AppShare> AppShares => Set<AppShare>();
    public DbSet<AppImport> AppImports => Set<AppImport>();
    public DbSet<AppInstallation> AppInstallations => Set<AppInstallation>();
    public DbSet<AppConfigurationRevision> AppConfigurationRevisions => Set<AppConfigurationRevision>();
    public DbSet<AppOperation> AppOperations => Set<AppOperation>();
    public DbSet<AppRun> AppRuns => Set<AppRun>();
    public DbSet<AppRunOutput> AppRunOutputs => Set<AppRunOutput>();
    public DbSet<AppArtifact> AppArtifacts => Set<AppArtifact>();
    public DbSet<AppIdempotencyRecord> AppIdempotencyRecords => Set<AppIdempotencyRecord>();
    public DbSet<RobotExecutionSlot> RobotExecutionSlots => Set<RobotExecutionSlot>();
    public DbSet<RobotAppInventory> RobotAppInventories => Set<RobotAppInventory>();
    public DbSet<AppDeviceReport> AppDeviceReports => Set<AppDeviceReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HumanInteraction>(entity =>
        {
            entity.ToTable("human_interactions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.ExecutionId).HasColumnName("execution_id");
            entity.Property(item => item.Prompt).HasColumnName("prompt");
            entity.Property(item => item.Choices).HasColumnName("choices").HasColumnType("jsonb");
            entity.Property(item => item.Deadline).HasColumnName("deadline");
            entity.Property(item => item.TimeoutOutcome).HasColumnName("timeout_outcome");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.Outcome).HasColumnName("outcome");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(item => new { item.RobotId, item.CreatedAt, item.Id });
            entity.HasIndex(item => item.ExecutionId);
            entity.HasOne(item => item.Robot).WithMany().HasForeignKey(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_human_interaction_state", "state IN ('pending','answered','timed_out','cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_human_interaction_outcome", "(state = 'pending' AND outcome IS NULL) OR (state = 'answered' AND outcome IS NOT NULL) OR (state = 'timed_out' AND outcome IS NOT NULL AND deadline IS NOT NULL AND timeout_outcome IS NOT NULL AND outcome = timeout_outcome) OR (state = 'cancelled' AND outcome IS NULL)"));
            entity.ToTable(table => table.HasCheckConstraint("ck_human_interaction_timeout", "(deadline IS NULL AND timeout_outcome IS NULL) OR (deadline IS NOT NULL AND timeout_outcome IS NOT NULL)"));
        });
        modelBuilder.Entity<HumanInteractionResponse>(entity =>
        {
            entity.ToTable("human_interaction_responses");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.InteractionId).HasColumnName("interaction_id");
            entity.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(item => item.ChosenValue).HasColumnName("chosen_value");
            entity.Property(item => item.Status).HasColumnName("status");
            entity.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint");
            entity.Property(item => item.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(item => item.ConfirmedAt).HasColumnName("confirmed_at");
            entity.HasIndex(item => item.InteractionId).IsUnique();
            entity.HasIndex(item => new { item.InteractionId, item.ActorUserId, item.IdempotencyKey }).IsUnique();
            entity.HasOne(item => item.Interaction).WithOne(item => item.Response).HasForeignKey<HumanInteractionResponse>(item => item.InteractionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Actor).WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_human_response_status", "status IN ('submitted','applied','rejected_timed_out','rejected_cancelled')"));
        });
        modelBuilder.Entity<MissionUiSession>(entity =>
        {
            entity.ToTable("mission_ui_sessions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.ExecutionId).HasColumnName("execution_id");
            entity.Property(item => item.OrganizationId).HasColumnName("organization_id");
            entity.Property(item => item.UserId).HasColumnName("user_id");
            entity.Property(item => item.BootstrapTokenHash).HasColumnName("bootstrap_token_hash");
            entity.Property(item => item.SessionTokenHash).HasColumnName("session_token_hash");
            entity.Property(item => item.BootstrapExpiresAt).HasColumnName("bootstrap_expires_at");
            entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.ExchangedAt).HasColumnName("exchanged_at");
            entity.Property(item => item.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(item => new { item.ExecutionId, item.UserId, item.ExpiresAt });
            entity.HasIndex(item => item.OrganizationId);
            entity.HasIndex(item => item.SessionTokenHash).IsUnique()
                .HasFilter("session_token_hash IS NOT NULL");
            entity.HasOne(item => item.Execution).WithMany().HasForeignKey(item => item.ExecutionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Organization).WithMany().HasForeignKey(item => item.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_mission_ui_session_hashes", "octet_length(bootstrap_token_hash) = 32 AND (session_token_hash IS NULL OR octet_length(session_token_hash) = 32) AND ((session_token_hash IS NULL AND exchanged_at IS NULL) OR (session_token_hash IS NOT NULL AND exchanged_at IS NOT NULL))"));
        });
        modelBuilder.Entity<LogbookEvent>(entity =>
        {
            entity.ToTable("logbook_events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.RobotId).HasColumnName("robot_id");
            entity.Property(item => item.OperationId).HasColumnName("operation_id");
            entity.Property(item => item.AppOperationId).HasColumnName("app_operation_id");
            entity.Property(item => item.AppInstallationId).HasColumnName("app_installation_id");
            entity.HasOne<AppOperation>().WithMany().HasForeignKey(item => item.AppOperationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppInstallation>().WithMany().HasForeignKey(item => item.AppInstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(item => item.LocalSequence).HasColumnName("local_sequence");
            entity.Property(item => item.Type).HasColumnName("type");
            entity.Property(item => item.SchemaVersion).HasColumnName("schema_version");
            entity.Property(item => item.OccurredAt).HasColumnName("occurred_at");
            entity.Property(item => item.ReceivedAt).HasColumnName("received_at");
            entity.Property(item => item.State).HasColumnName("state");
            entity.Property(item => item.ExecutionId).HasColumnName("execution_id");
            entity.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.HasIndex(item => new { item.RobotId, item.LocalSequence }).IsUnique();
            entity.HasIndex(item => new { item.RobotId, item.ReceivedAt, item.Id });
            entity.HasOne(item => item.Robot).WithMany().HasForeignKey(item => item.RobotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Operation).WithMany().HasForeignKey(item => item.OperationId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_sequence", "(local_sequence BETWEEN 1 AND 9007199254740991) OR (local_sequence IS NULL AND type IN ('human.interaction.requested','human.interaction.answered','human.interaction.timed_out','human.interaction.cancelled','mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped','app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted'))"));
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_state", "state IN ('accepted','running','succeeded','failed','cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_logbook_version", "(schema_version = 1 AND ((type = 'device.operation.state_changed' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NOT NULL AND execution_id IS NULL AND payload IS NULL) OR (type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (type = 'agent.motion.event' AND operation_id IS NULL AND state IS NULL AND execution_id IS NULL AND payload IS NOT NULL) OR (type LIKE 'human.interaction.%' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL) OR (type IN ('mission.revision_selected','mission.started','mission.completed','mission.failed','mission.stopped') AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL AND local_sequence IS NULL))) OR (schema_version = 2 AND type = 'mission.logbook.entry' AND (operation_id IS NOT NULL OR app_operation_id IS NOT NULL) AND state IS NULL AND execution_id IS NOT NULL AND payload IS NOT NULL) OR (schema_version = 3 AND app_operation_id IS NOT NULL AND operation_id IS NULL AND app_installation_id IS NOT NULL AND state IS NULL AND payload IS NOT NULL AND local_sequence IS NULL AND type IN ('app.installation.changed','app.run.started','app.run.completed','app.run.failed','app.run.stopped','app.run.interrupted'))"));
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
            entity.ToTable(table => table.HasCheckConstraint("ck_dev_run_generation", "generation > 0"));
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
            entity.Property(execution => execution.ArtifactState).HasColumnName("artifact_state");
            entity.Property(execution => execution.ArtifactError).HasColumnName("artifact_error").HasColumnType("jsonb");
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
            entity.ToTable(table => table.HasCheckConstraint("ck_script_execution_artifact_state", "artifact_state IN ('unknown', 'pending', 'uploading', 'ready', 'failed')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_script_execution_output", "octet_length(stdout) <= 65536 AND octet_length(stderr) <= 65536"));
        });
        modelBuilder.Entity<ScriptArtifact>(entity =>
        {
            entity.ToTable("script_artifacts");
            entity.HasKey(artifact => new { artifact.ExecutionId, artifact.Path });
            entity.Property(artifact => artifact.Id).HasColumnName("id");
            entity.Property(artifact => artifact.ExecutionId).HasColumnName("execution_id");
            entity.Property(artifact => artifact.Path).HasColumnName("path");
            entity.Property(artifact => artifact.Size).HasColumnName("size");
            entity.Property(artifact => artifact.Sha256).HasColumnName("sha256");
            entity.Property(artifact => artifact.ContentType).HasColumnName("content_type");
            entity.Property(artifact => artifact.State).HasColumnName("state");
            entity.HasIndex(artifact => artifact.Id).IsUnique();
            entity.HasOne(artifact => artifact.Execution).WithMany().HasForeignKey(artifact => artifact.ExecutionId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_script_artifact_size", "size BETWEEN 0 AND 9007199254740991"));
            entity.ToTable(table => table.HasCheckConstraint("ck_script_artifact_content_type", "content_type IN ('image/jpeg', 'image/png', 'application/octet-stream')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_script_artifact_state", "state IN ('pending', 'uploading', 'available', 'failed')"));
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
            entity.ToTable(table => table.HasCheckConstraint("ck_script_revision_size", "archive_size BETWEEN 1 AND 9007199254740991"));
        });
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id");
            entity.Property(user => user.Auth0Sub).HasColumnName("auth0_sub");
            entity.Property(user => user.Name).HasColumnName("name").HasMaxLength(120);
            entity.Property(user => user.Email).HasColumnName("email").HasMaxLength(320);
            entity.Property(user => user.ProfilePictureUrl).HasColumnName("profile_picture_url").HasMaxLength(2048);
            entity.Property(user => user.PhoneNumber).HasColumnName("phone_number").HasMaxLength(32);
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

        modelBuilder.Entity<OrganizationInvitation>(entity =>
        {
            entity.ToTable("organization_invitations");
            entity.HasKey(invitation => invitation.Id);
            entity.Property(invitation => invitation.Id).HasColumnName("id");
            entity.Property(invitation => invitation.OrganizationId).HasColumnName("organization_id");
            entity.Property(invitation => invitation.Email).HasColumnName("email");
            entity.Property(invitation => invitation.NormalizedEmail).HasColumnName("normalized_email");
            entity.Property(invitation => invitation.Role).HasColumnName("role").HasConversion<string>();
            entity.Property(invitation => invitation.InvitedByUserId).HasColumnName("invited_by_user_id");
            entity.Property(invitation => invitation.TokenHash).HasColumnName("token_hash");
            entity.Property(invitation => invitation.CreatedAt).HasColumnName("created_at");
            entity.Property(invitation => invitation.ExpiresAt).HasColumnName("expires_at");
            entity.Property(invitation => invitation.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(invitation => invitation.AcceptedByUserId).HasColumnName("accepted_by_user_id");
            entity.Property(invitation => invitation.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(invitation => invitation.TokenHash).IsUnique();
            entity.HasIndex(invitation => new { invitation.OrganizationId, invitation.NormalizedEmail })
                .IsUnique()
                .HasFilter("accepted_at IS NULL AND revoked_at IS NULL");
            entity.HasOne(invitation => invitation.Organization).WithMany().HasForeignKey(invitation => invitation.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(invitation => invitation.InvitedBy).WithMany().HasForeignKey(invitation => invitation.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(invitation => invitation.AcceptedBy).WithMany().HasForeignKey(invitation => invitation.AcceptedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("ck_organization_invitation_role", "role IN ('Owner', 'Developer', 'Operator')"));
            entity.ToTable(table => table.HasCheckConstraint("ck_organization_invitation_state", "(accepted_at IS NULL AND accepted_by_user_id IS NULL) OR (accepted_at IS NOT NULL AND accepted_by_user_id IS NOT NULL)"));
        });

        modelBuilder.Entity<Robot>(entity =>
        {
            entity.ToTable("robots");
            entity.HasKey(robot => robot.Id);
            entity.HasAlternateKey(robot => new { robot.Id, robot.OrganizationId });
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

        ConfigureApps(modelBuilder);
    }

    private static void ConfigureApps(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RobotAppInventory>(e =>
        {
            e.Property(x => x.RunsSupported).HasColumnName("runs_supported");
            e.Property(x => x.InstallationsSupported).HasColumnName("installations_supported");
            e.ToTable("robot_app_inventory"); e.HasKey(x => x.RobotId);
            e.Property(x => x.RobotId).HasColumnName("robot_id");
            e.Property(x => x.DeploymentNamespace).HasColumnName("deployment_namespace");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.OfferedVersions).HasColumnName("offered_versions").HasColumnType("jsonb");
            e.Property(x => x.Supported).HasColumnName("supported");
            e.Property(x => x.NegotiatedAt).HasColumnName("negotiated_at");
            e.Property(x => x.ReportSequence).HasColumnName("report_sequence");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.Property(x => x.Platform).HasColumnName("platform").HasColumnType("jsonb");
            e.Property(x => x.Inventory).HasColumnName("inventory").HasColumnType("jsonb");
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_inventory_sequence", "report_sequence BETWEEN 0 AND 9007199254740991"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_inventory_projection", "(report_sequence = 0 AND received_at IS NULL AND platform IS NULL AND inventory IS NULL) OR (report_sequence > 0 AND received_at IS NOT NULL AND platform IS NOT NULL AND inventory IS NOT NULL)"));
        });
        modelBuilder.Entity<AppDeviceReport>(e =>
        {
            e.ToTable("app_device_reports"); e.HasKey(x => new { x.RobotId, x.ReportId });
            e.Property(x => x.RobotId).HasColumnName("robot_id"); e.Property(x => x.ReportId).HasColumnName("report_id");
            e.Property(x => x.DeploymentNamespace).HasColumnName("deployment_namespace");
            e.Property(x => x.Route).HasColumnName("route").HasMaxLength(32);
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.BodySha256).HasColumnName("body_sha256").HasMaxLength(64);
            e.Property(x => x.Receipt).HasColumnName("receipt").HasColumnType("jsonb");
            e.HasIndex(x => new { x.RobotId, x.DeploymentNamespace, x.Route, x.Sequence }).IsUnique();
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_device_report_sequence", "sequence BETWEEN 1 AND 9007199254740991"));
        });
        modelBuilder.Entity<AppRecord>(e =>
        {
            e.ToTable("apps"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.PublisherOrganizationId).HasColumnName("publisher_organization_id");
            e.Property(x => x.AppKey).HasColumnName("app_key").HasMaxLength(100);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(4096);
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasAlternateKey(x => new { x.Id, x.PublisherOrganizationId });
            e.HasIndex(x => new { x.PublisherOrganizationId, x.AppKey }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.PublisherOrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_key", "app_key ~ '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$'"));
        });
        modelBuilder.Entity<AppRevision>(e =>
        {
            e.ToTable("app_revisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.AppId).HasColumnName("app_id");
            e.Property(x => x.PublisherOrganizationId).HasColumnName("publisher_organization_id");
            e.Property(x => x.Release).HasColumnName("release").HasMaxLength(100);
            e.Property(x => x.Manifest).HasColumnName("manifest").HasColumnType("jsonb");
            e.Property(x => x.ManifestDigest).HasColumnName("manifest_digest").HasMaxLength(71);
            e.Property(x => x.Visibility).HasColumnName("visibility").HasMaxLength(16);
            e.Property(x => x.PublishedByUserId).HasColumnName("published_by_user_id"); e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.HasAlternateKey(x => new { x.Id, x.AppId, x.PublisherOrganizationId });
            e.HasAlternateKey(x => new { x.Id, x.AppId });
            e.HasIndex(x => new { x.AppId, x.Release }).IsUnique();
            e.HasOne<AppRecord>().WithMany().HasForeignKey(x => new { x.AppId, x.PublisherOrganizationId }).HasPrincipalKey(x => new { x.Id, x.PublisherOrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PublishedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_revision_visibility", "visibility IN ('listed','unlisted')"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_revision_digest", "manifest_digest ~ '^sha256:[0-9a-f]{64}$'"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_revision_manifest_object", "jsonb_typeof(manifest) = 'object'"));
        });
        modelBuilder.Entity<AppShare>(e =>
        {
            e.ToTable("app_shares"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.AppId).HasColumnName("app_id");
            e.Property(x => x.RevisionId).HasColumnName("revision_id"); e.Property(x => x.PublisherOrganizationId).HasColumnName("publisher_organization_id");
            e.Property(x => x.DestinationOrganizationId).HasColumnName("destination_organization_id"); e.Property(x => x.GrantedByUserId).HasColumnName("granted_by_user_id");
            e.Property(x => x.GrantedAt).HasColumnName("granted_at"); e.Property(x => x.ExpiresAt).HasColumnName("expires_at"); e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasAlternateKey(x => new { x.Id, x.RevisionId, x.AppId, x.PublisherOrganizationId, x.DestinationOrganizationId });
            e.HasOne<AppRevision>().WithMany().HasForeignKey(x => new { x.RevisionId, x.AppId, x.PublisherOrganizationId }).HasPrincipalKey(x => new { x.Id, x.AppId, x.PublisherOrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.DestinationOrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_share_expiry", "expires_at > granted_at AND (revoked_at IS NULL OR revoked_at >= granted_at)"));
        });
        modelBuilder.Entity<AppImport>(e =>
        {
            e.ToTable("app_imports"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.AppId).HasColumnName("app_id"); e.Property(x => x.RevisionId).HasColumnName("revision_id");
            e.Property(x => x.PublisherOrganizationId).HasColumnName("publisher_organization_id"); e.Property(x => x.SourceKind).HasColumnName("source_kind").HasMaxLength(20);
            e.Property(x => x.ShareId).HasColumnName("share_id"); e.Property(x => x.ImportedByUserId).HasColumnName("imported_by_user_id"); e.Property(x => x.ImportedAt).HasColumnName("imported_at");
            e.HasIndex(x => new { x.OrganizationId, x.RevisionId }).IsUnique();
            e.HasAlternateKey(x => new { x.Id, x.AppId, x.OrganizationId });
            e.HasOne<AppRevision>().WithMany().HasForeignKey(x => new { x.RevisionId, x.AppId, x.PublisherOrganizationId }).HasPrincipalKey(x => new { x.Id, x.AppId, x.PublisherOrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppShare>().WithMany().HasForeignKey(x => new { x.ShareId, x.RevisionId, x.AppId, x.PublisherOrganizationId, DestinationOrganizationId = x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.RevisionId, x.AppId, x.PublisherOrganizationId, x.DestinationOrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ImportedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_import_source", "(source_kind = 'store' AND share_id IS NULL) OR (source_kind = 'direct_share' AND share_id IS NOT NULL)"));
        });
        modelBuilder.Entity<AppInstallation>(e =>
        {
            e.ToTable("app_installations"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.RobotId).HasColumnName("robot_id"); e.Property(x => x.AppId).HasColumnName("app_id"); e.Property(x => x.ImportId).HasColumnName("import_id"); e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.Property(x => x.AvailableRevisionId).HasColumnName("available_revision_id");
            e.Property(x => x.InstalledBinding).HasColumnName("installed_binding").HasColumnType("jsonb"); e.Property(x => x.Candidate).HasColumnName("candidate").HasColumnType("jsonb"); e.Property(x => x.LastObservedAt).HasColumnName("last_observed_at");
            e.HasIndex(x => new { x.RobotId, x.AppId }).IsUnique();
            e.HasAlternateKey(x => new { x.Id, x.RobotId, x.OrganizationId });
            e.HasAlternateKey(x => new { x.Id, x.AppId });
            e.HasOne<Robot>().WithMany().HasForeignKey(x => new { x.RobotId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppRecord>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppImport>().WithMany().HasForeignKey(x => new { x.ImportId, x.AppId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.AppId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppRevision>().WithMany().HasForeignKey(x => new { x.AvailableRevisionId, x.AppId }).HasPrincipalKey(x => new { x.Id, x.AppId }).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_installation_json_objects", "(installed_binding IS NULL OR jsonb_typeof(installed_binding) = 'object') AND (candidate IS NULL OR jsonb_typeof(candidate) = 'object')"));
        });
        modelBuilder.Entity<AppConfigurationRevision>(e =>
        {
            e.ToTable("app_configuration_revisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.InstallationId).HasColumnName("installation_id"); e.Property(x => x.AppId).HasColumnName("app_id"); e.Property(x => x.RevisionId).HasColumnName("revision_id");
            e.Property(x => x.Values).HasColumnName("values").HasColumnType("jsonb"); e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id"); e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<AppInstallation>().WithMany().HasForeignKey(x => new { x.InstallationId, x.AppId }).HasPrincipalKey(x => new { x.Id, x.AppId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppRevision>().WithMany().HasForeignKey(x => new { x.RevisionId, x.AppId }).HasPrincipalKey(x => new { x.Id, x.AppId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_configuration_values_object", "jsonb_typeof(values) = 'object'"));
        });
        modelBuilder.Entity<AppOperation>(e =>
        {
            e.Property(x => x.Command).HasColumnName("command").HasColumnType("jsonb");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.ToTable("app_operations"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.OrganizationId).HasColumnName("organization_id"); e.Property(x => x.RobotId).HasColumnName("robot_id");
            e.Property(x => x.InstallationId).HasColumnName("installation_id"); e.Property(x => x.RunId).HasColumnName("run_id"); e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(24);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(16); e.Property(x => x.Phase).HasColumnName("phase").HasMaxLength(32); e.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
            e.Property(x => x.RequestedAt).HasColumnName("requested_at"); e.Property(x => x.DeadlineAt).HasColumnName("deadline_at"); e.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
            e.Property(x => x.ObservedAt).HasColumnName("observed_at"); e.Property(x => x.ReceivedAt).HasColumnName("received_at"); e.Property(x => x.CompletedAt).HasColumnName("completed_at"); e.Property(x => x.Failure).HasColumnName("failure").HasColumnType("jsonb");
            e.HasIndex(x => new { x.RobotId, x.RequestedAt, x.Id }); e.HasIndex(x => x.InstallationId).IsUnique().HasFilter("state IN ('queued','accepted','running') AND run_id IS NULL");
            e.HasOne<Robot>().WithMany().HasForeignKey(x => new { x.RobotId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppInstallation>().WithMany().HasForeignKey(x => new { x.InstallationId, x.RobotId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.RobotId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppRun>().WithMany().HasForeignKey(x => new { x.RunId, x.InstallationId, x.RobotId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.InstallationId, x.RobotId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_operation_state", "state IN ('queued','accepted','running','succeeded','failed','cancelled')"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_operation_deadline", "deadline_at >= requested_at"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_operation_failure_object", "failure IS NULL OR jsonb_typeof(failure) = 'object'"));
        });
        modelBuilder.Entity<AppRun>(e =>
        {
            e.ToTable("app_runs"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.OrganizationId).HasColumnName("organization_id"); e.Property(x => x.RobotId).HasColumnName("robot_id");
            e.Property(x => x.InstallationId).HasColumnName("installation_id"); e.Property(x => x.StartOperationId).HasColumnName("start_operation_id"); e.Property(x => x.Snapshot).HasColumnName("snapshot").HasColumnType("jsonb");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(16); e.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id"); e.Property(x => x.RequestedAt).HasColumnName("requested_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at"); e.Property(x => x.EndedAt).HasColumnName("ended_at"); e.Property(x => x.ObservedAt).HasColumnName("observed_at"); e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.Property(x => x.ExitCode).HasColumnName("exit_code"); e.Property(x => x.Failure).HasColumnName("failure").HasColumnType("jsonb"); e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(x => x.StartOperationId).IsUnique(); e.HasIndex(x => new { x.RobotId, x.RequestedAt, x.Id });
            e.HasAlternateKey(x => new { x.Id, x.InstallationId, x.RobotId, x.OrganizationId });
            e.HasOne<Robot>().WithMany().HasForeignKey(x => new { x.RobotId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppInstallation>().WithMany().HasForeignKey(x => new { x.InstallationId, x.RobotId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.RobotId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppOperation>().WithMany().HasForeignKey(x => x.StartOperationId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_run_state", "state IN ('accepted','running','succeeded','failed','stopped','interrupted')"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_run_completion", "(state IN ('accepted','running') AND completed_at IS NULL) OR (state IN ('succeeded','failed','stopped','interrupted') AND completed_at IS NOT NULL)"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_run_snapshot_object", "jsonb_typeof(snapshot) = 'object'"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_run_failure_object", "failure IS NULL OR jsonb_typeof(failure) = 'object'"));
        });
        modelBuilder.Entity<AppRunOutput>(e =>
        {
            e.ToTable("app_run_outputs", t => t.HasCheckConstraint("ck_app_output_bounds", "octet_length(stdout) <= 65536 AND octet_length(stderr) <= 65536"));
            e.HasKey(x => x.RunId); e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Stdout).HasColumnName("stdout"); e.Property(x => x.Stderr).HasColumnName("stderr");
            e.Property(x => x.StdoutSealed).HasColumnName("stdout_sealed"); e.Property(x => x.StderrSealed).HasColumnName("stderr_sealed");
            e.Property(x => x.StdoutTruncated).HasColumnName("stdout_truncated"); e.Property(x => x.StderrTruncated).HasColumnName("stderr_truncated");
            e.HasOne<AppRun>().WithOne().HasForeignKey<AppRunOutput>(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AppArtifact>(e =>
        {
            e.ToTable("app_artifacts"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Path).HasColumnName("path").HasMaxLength(240); e.Property(x => x.Size).HasColumnName("size");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64); e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(80);
            e.HasIndex(x => new { x.RunId, x.Path }).IsUnique(); e.HasOne<AppRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AppIdempotencyRecord>(e =>
        {
            e.ToTable("app_idempotency_records"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.PrincipalUserId).HasColumnName("principal_user_id"); e.Property(x => x.Route).HasColumnName("route").HasMaxLength(200);
            e.Property(x => x.Target).HasColumnName("target").HasMaxLength(200); e.Property(x => x.Key).HasColumnName("key").HasMaxLength(200); e.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            e.Property(x => x.Outcome).HasColumnName("outcome").HasColumnType("jsonb"); e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.PrincipalUserId, x.Route, x.Target, x.Key }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PrincipalUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_app_idempotency_key", "length(key) BETWEEN 1 AND 200"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_idempotency_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'"));
            e.ToTable(t => t.HasCheckConstraint("ck_app_idempotency_outcome_object", "jsonb_typeof(outcome) = 'object'"));
        });
        modelBuilder.Entity<RobotExecutionSlot>(e =>
        {
            e.ToTable("robot_execution_slots"); e.HasKey(x => x.RobotId);
            e.Property(x => x.RobotId).HasColumnName("robot_id"); e.Property(x => x.Source).HasColumnName("source").HasMaxLength(16); e.Property(x => x.ExecutionId).HasColumnName("execution_id"); e.Property(x => x.ClaimedAt).HasColumnName("claimed_at");
            e.HasOne<Robot>().WithMany().HasForeignKey(x => x.RobotId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_robot_execution_slot_source", "source IN ('legacy','app')"));
        });
    }
}
