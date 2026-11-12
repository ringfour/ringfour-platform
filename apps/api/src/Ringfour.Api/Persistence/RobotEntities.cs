namespace Ringfour.Api.Persistence;

// Audit event types recorded in robot_audit_events. "enrolled" is
// device-attributed and therefore has a null actor; the rest record the
// acting user.
public static class RobotAuditTypes
{
    public const string RobotCreated = "robot_created";
    public const string EnrollmentTokenIssued = "enrollment_token_issued";
    public const string Enrolled = "enrolled";
    public const string CredentialRotated = "credential_rotated";
    public const string CredentialRevoked = "credential_revoked";
}

public sealed class Robot
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public string? ProfileRef { get; set; }

    public Organization Organization { get; set; } = null!;
    public List<EnrollmentToken> EnrollmentTokens { get; } = [];
    public List<RobotCredential> Credentials { get; } = [];
    public List<RobotAuditEvent> AuditEvents { get; } = [];
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public required string TokenHash { get; set; }
    public Guid IssuedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public Robot Robot { get; set; } = null!;
    public User IssuedBy { get; set; } = null!;
}

public sealed class RobotCredential
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public required string SecretHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Robot Robot { get; set; } = null!;
}

public sealed class DevicePairingRequest
{
    public Guid Id { get; set; }
    public required string CodeHash { get; set; }
    public required string CredentialHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public Guid? RobotId { get; set; }
    public Guid? CredentialId { get; set; }
    public Robot? Robot { get; set; }
    public RobotCredential? Credential { get; set; }
}

public sealed class RobotAuditEvent
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string Type { get; set; }
    public Guid? ActorUserId { get; set; }

    public Robot Robot { get; set; } = null!;
    public User? Actor { get; set; }
}
