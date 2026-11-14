using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Missions;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.DevSessions;

public static class DevSessionEndpoints
{
    private const string CreateRoute = "POST /v1/robots/{robot_id}/dev-sessions";
    private const string CloseRoute = "DELETE /v1/dev-sessions/{session_id}";

    public static void MapDevSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var human = endpoints.MapGroup("/v1").RequireAuthorization();
        human.MapPost("/robots/{robotId:guid}/dev-sessions", Create);
        human.MapGet("/dev-sessions/{sessionId:guid}", Get);
        human.MapDelete("/dev-sessions/{sessionId:guid}", Close);
    }

    private static async Task<IResult> Create(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration config, Guid robotId, CancellationToken ct)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, robotId, OrgRole.Developer, ct);
        var key = DeploymentEndpoints.Key(context);
        JsonObject body;
        try { body = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid development session request."); }
        if (body.Count != 1 || body["ttl_seconds"] is not JsonValue value || !value.TryGetValue<int>(out var ttl) || ttl is < 300 or > 28800)
            throw ApiException.InvalidInput("Invalid development session request.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ttl.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var clock = DateTimeOffset.UtcNow;
        // PostgreSQL retains microseconds. Commands must use the same instant
        // before and after reload so the daemon and Runner see identical expiry.
        var now = new DateTimeOffset(clock.Ticks - clock.Ticks % 10, TimeSpan.Zero);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(item => item.PrincipalUserId == user.Id &&
            item.Route == CreateRoute && item.Target == robotId.ToString() && item.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
            var original = await db.Operations.SingleAsync(item => item.Id == previous.OutcomeOperationId, ct);
            var sessionId = Guid.Parse(JsonNode.Parse(original.Payload)!["session_id"]!.GetValue<string>());
            await tx.CommitAsync(ct);
            return Results.Json(Accepted(sessionId, original.Id), RingfourJson.Options, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        var presence = await db.RobotPresences.SingleOrDefaultAsync(item => item.RobotId == robotId, ct);
        if (manager.Get(robotId) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now, config.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
            throw new ApiException(503, "robot_unavailable", "The robot is offline.");
        if (await db.DevSessions.AnyAsync(item => item.RobotId == robotId && item.State != "closed" && item.State != "expired", ct))
            throw ApiException.Conflict("execution_conflict", "A development session is already open.");
        if (await db.DevSessionRuns.AnyAsync(item => item.RobotId == robotId && (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct))
            throw ApiException.Conflict("execution_conflict", "A development execution is active.");
        if (await db.ScriptExecutions.AnyAsync(item => item.RobotId == robotId && (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct))
            throw ApiException.Conflict("execution_conflict", "Another execution is active.");
        var session = new DevSession { Id = Guid.NewGuid(), RobotId = robotId, OwnerUserId = user.Id, State = "creating",
            Generation = 0, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddSeconds(ttl) };
        var operation = NewLifecycle(session, "create", now, user.Id);
        session.LastOperationId = operation.Id;
        db.DevSessions.Add(session);
        db.Operations.Add(operation);
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord { Id = Guid.NewGuid(), PrincipalUserId = user.Id,
            Route = CreateRoute, Target = robotId.ToString(), Key = key, RequestFingerprint = fingerprint,
            OutcomeOperationId = operation.Id, CreatedAt = now, ExpiresAt = now.AddHours(config.GetValue("Idempotency:TtlHours", 24)) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await OperationEndpoints.DispatchAsync(db, manager, robotId, operation.Id, ct);
        return Results.Json(Accepted(session.Id, operation.Id), RingfourJson.Options, statusCode: 202);
    }

    private static async Task<(User User, DevSession Session)> Access(HttpContext context, RingfourDbContext db, Guid id, CancellationToken ct)
    {
        var session = await db.DevSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var (user, _) = await RobotAccess.RequireRobotAccess(context, db, session.RobotId, OrgRole.Operator, ct);
        return (user, session);
    }

    private static async Task<IResult> Get(HttpContext context, RingfourDbContext db, Guid sessionId, CancellationToken ct)
    {
        var (_, session) = await Access(context, db, sessionId, ct);
        return Results.Ok(new { id = session.Id, robot_id = session.RobotId, owner_user_id = session.OwnerUserId,
            state = session.State, generation = session.Generation, active_generation = session.ActiveGeneration,
            dependency_digest = session.DependencyDigest, expires_at = session.ExpiresAt, last_operation_id = session.LastOperationId,
            last_execution_id = session.LastExecutionId, last_error = session.LastError is null ? null : JsonNode.Parse(session.LastError),
            created_at = session.CreatedAt, updated_at = session.UpdatedAt });
    }

    private static async Task<IResult> Close(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration config, Guid sessionId, CancellationToken ct)
    {
        var (user, snapshot) = await Access(context, db, sessionId, ct);
        var key = DeploymentEndpoints.Key(context);
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {snapshot.RobotId} FOR UPDATE", ct);
        var session = await db.DevSessions.SingleAsync(item => item.Id == sessionId, ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(item => item.PrincipalUserId == user.Id &&
            item.Route == CloseRoute && item.Target == sessionId.ToString() && item.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            await tx.CommitAsync(ct);
            return Results.Json(Accepted(sessionId, previous.OutcomeOperationId), RingfourJson.Options, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        if (session.State is "closed" or "expired" or "closing")
            throw ApiException.Conflict("session_unavailable", "The development session is closing or closed.");
        var operation = NewLifecycle(session, "close", now, user.Id);
        session.State = "closing";
        session.LastOperationId = operation.Id;
        session.UpdatedAt = now;
        db.Operations.Add(operation);
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord { Id = Guid.NewGuid(), PrincipalUserId = user.Id,
            Route = CloseRoute, Target = sessionId.ToString(), Key = key, RequestFingerprint = "close",
            OutcomeOperationId = operation.Id, CreatedAt = now, ExpiresAt = now.AddHours(config.GetValue("Idempotency:TtlHours", 24)) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await OperationEndpoints.DispatchAsync(db, manager, session.RobotId, operation.Id, ct);
        return Results.Json(Accepted(session.Id, operation.Id), RingfourJson.Options, statusCode: 202);
    }

    internal static Operation NewLifecycle(DevSession session, string reason, DateTimeOffset now, Guid? actor)
    {
        var id = Guid.NewGuid();
        return new Operation { Id = id, RobotId = session.RobotId, Kind = "dev.session." + reason, PayloadVersion = 1,
            Payload = JsonSerializer.Serialize(new { operation_id = id, session_id = session.Id,
                expires_at = RingfourJson.UtcTimestamp(session.ExpiresAt), reason }, RingfourJson.Options),
            State = OperationState.Queued, CreatedAt = now, UpdatedAt = now,
            DeadlineAt = reason == "create" ? session.ExpiresAt : now.AddYears(10), RequestedByUserId = actor };
    }

    private static object Accepted(Guid session, Guid operation) => new { session_id = session, operation_id = operation, execution_id = (Guid?)null };
}
