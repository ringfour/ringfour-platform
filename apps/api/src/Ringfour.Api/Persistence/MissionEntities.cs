namespace Ringfour.Api.Persistence;

public sealed class Mission
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public required string Name { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public int LatestVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Organization Organization { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
}

public sealed class MissionRevision
{
    public Guid Id { get; set; }
    public Guid MissionId { get; set; }
    public Guid ScriptRevisionId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public int Version { get; set; }
    public required string Manifest { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Mission Mission { get; set; } = null!;
    public ScriptRevision ScriptRevision { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
}

public sealed class MissionDeployment
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public Guid? SelectedRevisionId { get; set; }
    public Guid? CandidateRevisionId { get; set; }
    public Guid? CandidateOperationId { get; set; }
    public required string DesiredState { get; set; }
    public required string ObservedState { get; set; }
    public Guid? LastOperationId { get; set; }
    public Guid? LastExecutionId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Robot Robot { get; set; } = null!;
    public MissionRevision? SelectedRevision { get; set; }
    public MissionRevision? CandidateRevision { get; set; }
}

public sealed class DeploymentAction
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid ActorUserId { get; set; }
    public required string Kind { get; set; }
    public required string State { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? TargetExecutionId { get; set; }
    public Guid? StopOperationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public MissionDeployment Deployment { get; set; } = null!;
    public User Actor { get; set; } = null!;
    public Operation Operation { get; set; } = null!;
}
