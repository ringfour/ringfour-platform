namespace Ringfour.Api.Persistence;

// Persistent workflow foundation; development-session endpoints are not exposed yet.
public sealed class DevSession
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public Guid OwnerUserId { get; set; }
    public required string State { get; set; }
    public int Generation { get; set; }
    public int? ActiveGeneration { get; set; }
    public string? DependencyDigest { get; set; }
    public Guid? LastOperationId { get; set; }
    public Guid? LastExecutionId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Robot Robot { get; set; } = null!;
    public User Owner { get; set; } = null!;
}

public sealed class DevSessionGeneration
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid OperationId { get; set; }
    public int Generation { get; set; }
    public required string State { get; set; }
    public required string ArchiveSha256 { get; set; }
    public int ArchiveSize { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DevSession Session { get; set; } = null!;
    public Operation Operation { get; set; } = null!;
}

public sealed class DevLocalEndpoint
{
    public Guid RobotId { get; set; }
    public Guid CredentialId { get; set; }
    public required string CertificateSha256 { get; set; }
    public string? DaemonUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Robot Robot { get; set; } = null!;
    public RobotCredential Credential { get; set; } = null!;
}

public sealed class DevLocalGrant
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid OperationId { get; set; }
    public required string CertificateSha256 { get; set; }
    public string? DaemonUrl { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DevSession Session { get; set; } = null!;
    public Operation Operation { get; set; } = null!;
}

public sealed class DevSessionRun
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid RobotId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid OperationId { get; set; }
    public int Generation { get; set; }
    public required string State { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DevSession Session { get; set; } = null!;
    public Robot Robot { get; set; } = null!;
    public User Actor { get; set; } = null!;
    public Operation Operation { get; set; } = null!;
}
