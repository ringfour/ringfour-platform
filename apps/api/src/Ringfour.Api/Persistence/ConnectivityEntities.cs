namespace Ringfour.Api.Persistence;

public sealed class RobotPresence
{
    public Guid RobotId { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? DaemonVersion { get; set; }
    public int? ProtocolVersion { get; set; }

    public Robot Robot { get; set; } = null!;
}
