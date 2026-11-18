namespace Ringfour.Api.Persistence;

public sealed class HumanInteraction
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public Guid ExecutionId { get; set; }
    public required string Prompt { get; set; }
    public required string Choices { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public string? TimeoutOutcome { get; set; }
    public required string State { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Robot Robot { get; set; } = null!;
    public HumanInteractionResponse? Response { get; set; }
}

public sealed class HumanInteractionResponse
{
    public Guid Id { get; set; }
    public Guid InteractionId { get; set; }
    public Guid ActorUserId { get; set; }
    public required string ChosenValue { get; set; }
    public required string Status { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public HumanInteraction Interaction { get; set; } = null!;
    public User Actor { get; set; } = null!;
}
