using System.Text.Json.Nodes;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Connectivity;

public sealed record RobotStatusResponse(
    Guid RobotId,
    bool Online,
    DateTimeOffset? LastSeenAt,
    string? DaemonVersion,
    int? ProtocolVersion);

// Payload and Result are raw JSONB values; JsonNode keeps them embedded as
// JSON objects in the response instead of string-encoded.
public sealed record OperationResponse(
    Guid Id,
    Guid RobotId,
    string Kind,
    int PayloadVersion,
    JsonNode Payload,
    OperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset DeadlineAt,
    DateTimeOffset? AcknowledgedAt,
    JsonNode? Result,
    Guid? RequestedBy)
{
    public static OperationResponse FromEntity(Operation operation) => new(
        operation.Id,
        operation.RobotId,
        operation.Kind,
        operation.PayloadVersion,
        JsonNode.Parse(operation.Payload) ?? new JsonObject(),
        operation.State,
        operation.CreatedAt,
        operation.UpdatedAt,
        operation.DeadlineAt,
        operation.AcknowledgedAt,
        operation.Result is null ? null : JsonNode.Parse(operation.Result),
        operation.RequestedByUserId);
}

public sealed record CreateOperationRequest(string? Kind, int? PayloadVersion, JsonNode? Payload);
