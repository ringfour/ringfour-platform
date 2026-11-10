namespace Ringfour.Api.Persistence;

// Hosted operation lifecycle. The platform owns queued; the device reports
// everything from accepted onward. Terminal states are immutable.
public enum OperationState
{
    Queued,
    Accepted,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public static class OperationStates
{
    public static bool IsTerminal(OperationState state) =>
        state is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled;
}

// One row per robot; an accepted connection replaces the session id, and only
// the current session may advance last_seen_at (fenced updates in SQL).
public sealed class RobotPresence
{
    public Guid RobotId { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? DaemonVersion { get; set; }
    public int? ProtocolVersion { get; set; }

    public Robot Robot { get; set; } = null!;
}

public sealed class Operation
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public required string Kind { get; set; }
    public int PayloadVersion { get; set; }
    public required string Payload { get; set; }
    public OperationState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? Result { get; set; }

    public Robot Robot { get; set; } = null!;
    public User? RequestedBy { get; set; }
}

// Idempotency-Key records scoped to principal, route and target. The outcome
// references an operation id only; responses carrying one-time credentials are
// never persisted for replay.
public sealed class OperationIdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid PrincipalUserId { get; set; }
    public required string Route { get; set; }
    public required string Target { get; set; }
    public required string Key { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid OutcomeOperationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public User Principal { get; set; } = null!;
    public Operation Outcome { get; set; } = null!;
}
