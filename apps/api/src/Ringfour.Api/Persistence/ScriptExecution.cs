namespace Ringfour.Api.Persistence;

public sealed class ScriptExecution
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid? MissionRevisionId { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public byte[] Stdout { get; set; } = [];
    public byte[] Stderr { get; set; } = [];
    public bool StdoutTruncated { get; set; }
    public bool StderrTruncated { get; set; }
    public Robot Robot { get; set; } = null!;
    public ScriptRevision Revision { get; set; } = null!;
    public User Actor { get; set; } = null!;
    public Operation Operation { get; set; } = null!;
    public MissionDeployment? Deployment { get; set; }
    public MissionRevision? MissionRevision { get; set; }
}

public sealed class ScriptArtifact
{
    public Guid ExecutionId { get; set; }
    public required string Path { get; set; }
    public int Size { get; set; }
    public required string Sha256 { get; set; }
    public ScriptExecution Execution { get; set; } = null!;
}

public sealed class ScriptExecutionEvent
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid ActorUserId { get; set; }
    public required string State { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public ScriptExecution Execution { get; set; } = null!;
    public User Actor { get; set; } = null!;
}
