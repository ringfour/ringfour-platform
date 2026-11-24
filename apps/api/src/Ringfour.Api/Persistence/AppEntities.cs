namespace Ringfour.Api.Persistence;

public sealed class AppRecord
{
    public Guid Id { get; set; }
    public Guid PublisherOrganizationId { get; set; }
    public string AppKey { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppRevision
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public Guid PublisherOrganizationId { get; set; }
    public string Release { get; set; } = null!;
    public string Manifest { get; set; } = null!;
    public string ManifestDigest { get; set; } = null!;
    public string Visibility { get; set; } = "unlisted";
    public Guid PublishedByUserId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

public sealed class AppShare
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid PublisherOrganizationId { get; set; }
    public Guid DestinationOrganizationId { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AppImport
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AppId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid PublisherOrganizationId { get; set; }
    public string SourceKind { get; set; } = "store";
    public Guid? ShareId { get; set; }
    public Guid ImportedByUserId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class AppInstallation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RobotId { get; set; }
    public Guid AppId { get; set; }
    public Guid ImportId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? AvailableRevisionId { get; set; }
    public string? InstalledBinding { get; set; }
    public string? Candidate { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
}

public sealed class AppConfigurationRevision
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public Guid AppId { get; set; }
    public Guid RevisionId { get; set; }
    public string Values { get; set; } = "{}";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppOperation
{
    public string? Command { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RobotId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid? RunId { get; set; }
    public string Kind { get; set; } = null!;
    public string State { get; set; } = "queued";
    public string? Phase { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Failure { get; set; }
}

public sealed class AppRun
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RobotId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid StartOperationId { get; set; }
    public string Snapshot { get; set; } = "{}";
    public string State { get; set; } = "accepted";
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Failure { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AppIdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid PrincipalUserId { get; set; }
    public string Route { get; set; } = null!;
    public string Target { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string RequestFingerprint { get; set; } = null!;
    public string Outcome { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RobotExecutionSlot
{
    public Guid RobotId { get; set; }
    public string Source { get; set; } = null!;
    public Guid ExecutionId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}
