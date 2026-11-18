namespace Ringfour.Api.Persistence;

public sealed class MissionUiSession
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public required byte[] BootstrapTokenHash { get; set; }
    public byte[]? SessionTokenHash { get; set; }
    public DateTimeOffset BootstrapExpiresAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExchangedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public ScriptExecution Execution { get; set; } = null!;
    public Organization Organization { get; set; } = null!;
    public User User { get; set; } = null!;
}
