using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Connectivity;

public static class OperationEndpoints
{
    private const string CreateOperationRoute = "POST /v1/robots/{robot_id}/operations";

    public static void MapOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();

        group.MapGet("/robots/{robotId:guid}/status", GetStatus);
        group.MapPost("/robots/{robotId:guid}/operations", CreateOperation);
        group.MapGet("/robots/{robotId:guid}/operations/{operationId:guid}", GetOperation);
    }

    // Online is derived from server receive time, never from what the device
    // claims; a connected robot that stops sending goes offline purely by the
    // freshness window.
    public static bool ComputeOnline(DateTimeOffset? lastSeenAt, DateTimeOffset now, int offlineThresholdSeconds) =>
        lastSeenAt is not null && now - lastSeenAt.Value < TimeSpan.FromSeconds(offlineThresholdSeconds);

    private static async Task<RobotStatusResponse> GetStatus(
        HttpContext httpContext,
        RingfourDbContext db,
        IConfiguration configuration,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (_, robot) = await RobotAccess.RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Operator, cancellationToken);
        var threshold = configuration.GetValue("Connectivity:OfflineThresholdSeconds", 45);
        var presence = await db.RobotPresences.SingleOrDefaultAsync(
            candidate => candidate.RobotId == robot.Id, cancellationToken);
        return new RobotStatusResponse(
            robot.Id,
            ComputeOnline(presence?.LastSeenAt, DateTimeOffset.UtcNow, threshold),
            presence?.LastSeenAt,
            presence?.DaemonVersion,
            presence?.ProtocolVersion);
    }

    private static async Task<IResult> CreateOperation(
        HttpContext httpContext,
        RingfourDbContext db,
        DeviceConnectionManager manager,
        IConfiguration configuration,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Operator, cancellationToken);

        var rawBody = await new StreamReader(httpContext.Request.Body).ReadToEndAsync(cancellationToken);
        CreateOperationRequest request;
        try
        {
            var document = JsonNode.Parse(rawBody) as JsonObject;
            if (document is null || document.Count != 3 || document.Any(pair => pair.Key is not
                    ("kind" or "payload_version" or "payload")))
                throw new JsonException("Request must contain only kind, payload_version and payload.");
            request = JsonSerializer.Deserialize<CreateOperationRequest>(rawBody, RingfourJson.Options)
                ?? throw new JsonException("Request body must be a JSON object.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw ApiException.InvalidInput($"Request body is not valid JSON: {exception.Message}");
        }
        if (request.Kind is null || request.PayloadVersion != 1 || request.Payload is not JsonObject payload ||
            !ValidDiagnosticOperation(request.Kind, payload))
        {
            throw ApiException.InvalidInput("Unsupported diagnostic operation or invalid payload.");
        }
        if (request.Kind != "diagnostic.execution.stop")
            await RobotAccess.RequireRobotAccess(httpContext, db, robotId, OrgRole.Developer, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        string? fingerprint = null;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (idempotencyKey.Length > 200)
            {
                throw ApiException.InvalidInput("Idempotency-Key must not exceed 200 characters.");
            }
            fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

            var existing = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(
                record =>
                    record.PrincipalUserId == user.Id &&
                    record.Route == CreateOperationRoute &&
                    record.Target == robot.Id.ToString() &&
                    record.Key == idempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                if (existing.ExpiresAt > now)
                {
                    if (existing.RequestFingerprint != fingerprint)
                    {
                        throw ApiException.Conflict(
                            "conflict", "This Idempotency-Key was already used with a different request body.");
                    }
                    var replayed = await db.Operations.SingleAsync(
                        operation => operation.Id == existing.OutcomeOperationId, cancellationToken);
                    return Results.Ok(OperationResponse.FromEntity(replayed));
                }
                // Expired record: fall through and overwrite below.
                db.OperationIdempotencyRecords.Remove(existing);
            }
        }

        var deadline = now.AddSeconds(configuration.GetValue("Connectivity:OperationDeadlineSeconds", 60));
        var operation = new Operation
        {
            Id = Guid.NewGuid(),
            RobotId = robot.Id,
            Kind = request.Kind,
            PayloadVersion = request.PayloadVersion.Value,
            Payload = request.Payload.ToJsonString(RingfourJson.Options),
            State = OperationState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            DeadlineAt = deadline,
            RequestedByUserId = user.Id,
            AcknowledgedAt = null,
            Result = null,
        };
        db.Operations.Add(operation);
        if (fingerprint is not null)
        {
            db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord
            {
                Id = Guid.NewGuid(),
                PrincipalUserId = user.Id,
                Route = CreateOperationRoute,
                Target = robot.Id.ToString(),
                Key = idempotencyKey,
                RequestFingerprint = fingerprint,
                OutcomeOperationId = operation.Id,
                CreatedAt = now,
                ExpiresAt = now.AddHours(configuration.GetValue("Idempotency:TtlHours", 24)),
            });
        }

        try
        {
            // The operation is committed before dispatch; a lost push is
            // recovered by sweeper redelivery and reconnect replay.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IdentityAccess.IsUniqueViolation(exception))
        {
            // A concurrent request with the same key committed first; replay it.
            db.Entry(operation).State = EntityState.Detached;
            var winner = await db.OperationIdempotencyRecords.SingleAsync(
                record =>
                    record.PrincipalUserId == user.Id &&
                    record.Route == CreateOperationRoute &&
                    record.Target == robot.Id.ToString() &&
                    record.Key == idempotencyKey,
                cancellationToken);
            if (winner.RequestFingerprint != fingerprint)
            {
                throw ApiException.Conflict(
                    "conflict", "This Idempotency-Key was already used with a different request body.");
            }
            var replayed = await db.Operations.SingleAsync(
                candidate => candidate.Id == winner.OutcomeOperationId, cancellationToken);
            return Results.Ok(OperationResponse.FromEntity(replayed));
        }

        await DispatchAsync(db, manager, robot.Id, operation.Id, cancellationToken);
        return Results.Created(
            $"/v1/robots/{robot.Id}/operations/{operation.Id}",
            OperationResponse.FromEntity(operation));
    }

    private static bool ValidDiagnosticOperation(string? kind, JsonObject payload)
    {
        try
        {
            static bool ValidId(JsonNode? value) => value is not null &&
                Guid.TryParseExact(value.GetValue<string>(), "D", out var id) && id.ToString() == value.GetValue<string>();
            return kind switch
            {
                "diagnostic.noop" => true,
                "diagnostic.execution.start" => payload.Count == 1 && payload["fixture"]?.GetValue<string>() is "hello" or "long",
                "diagnostic.execution.stop" => payload.Count == 1 && ValidId(payload["execution_id"]),
                "diagnostic.execution.inspect" => payload.Count == 4 && ValidId(payload["execution_id"]) &&
                    payload["stream"]?.GetValue<string>() is "stdout" or "stderr" &&
                    payload["offset"]?.GetValue<int>() is >= 0 and <= 65536 && payload["limit"]?.GetValue<int>() is >= 1 and <= 16384,
                _ => false,
            };
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<OperationResponse> GetOperation(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid robotId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var (_, robot) = await RobotAccess.RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Operator, cancellationToken);
        var operation = await db.Operations.SingleOrDefaultAsync(
            candidate => candidate.Id == operationId && candidate.RobotId == robot.Id,
            cancellationToken);
        if (operation is null)
        {
            throw ApiException.NotFound(
                "The requested resource does not exist in this organization.");
        }
        return OperationResponse.FromEntity(operation);
    }

    // Pushes one committed operation to the robot's live session, if any. A
    // failed send leaves the operation queued; redelivery is at-least-once.
    public static async Task DispatchAsync(
        RingfourDbContext db,
        DeviceConnectionManager manager,
        Guid robotId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var session = manager.Get(robotId);
        if (session is null)
        {
            return;
        }
        var operation = await db.Operations.SingleOrDefaultAsync(
            candidate => candidate.Id == operationId, cancellationToken);
        if (operation is null || operation.State != OperationState.Queued)
        {
            return;
        }
        try
        {
            await manager.SendAsync(
                session, RingfourJson.Options, "command", DeviceSocketEndpoints.CommandPayload(operation));
        }
        catch (WebSocketException)
        {
            // The session will be unregistered by its receive loop; the queued
            // operation is replayed on the next connection.
        }
    }
}
