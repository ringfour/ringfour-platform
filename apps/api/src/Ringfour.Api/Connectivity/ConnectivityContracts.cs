using System.Text.Json.Nodes;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Connectivity;

public sealed record RobotStatusResponse(
    Guid RobotId,
    bool Online,
    DateTimeOffset? LastSeenAt,
    string? DaemonVersion,
    int? ProtocolVersion);
