namespace Ringfour.Api.Persistence;

public sealed class LogbookEvent
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public Guid? OperationId { get; set; }
    public long LocalSequence { get; set; }
    public required string Type { get; set; }
    public int SchemaVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? State { get; set; }
    public Guid? ExecutionId { get; set; }
    public string? Payload { get; set; }
    public Robot Robot { get; set; } = null!;
    public Operation? Operation { get; set; }
}

public sealed class LogbookCheckpoint
{
    public Guid RobotId { get; set; }
    public long Sequence { get; set; }
    public Robot Robot { get; set; } = null!;
}
