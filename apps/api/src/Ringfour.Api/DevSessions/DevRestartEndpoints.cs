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

public static class DevRestartEndpoints
{
    private const string Route = "POST /v1/dev-sessions/{session_id}/restart";

    public static void MapDevRestartEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/v1/dev-sessions/{sessionId:guid}/restart", Restart).RequireAuthorization();

    private static async Task<IResult> Restart(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration config, Guid sessionId, CancellationToken ct)
    {
        var session = await db.DevSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, session.RobotId, OrgRole.Developer, ct);
        var membership = await IdentityAccess.RequireMembership(db, robot.OrganizationId, user.Id, OrgRole.Developer, ct);
        if (session.OwnerUserId != user.Id && membership.Role != OrgRole.Owner)
            throw ApiException.Forbidden("Only the session owner or an organization owner may restart it.");
        var key = DeploymentEndpoints.Key(context);
        JsonObject body;
        try { body = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid development restart request."); }
        if (body.Count != 1 || body["generation"] is not JsonValue value || !value.TryGetValue<int>(out var generation) || generation is < 1 or > 20)
            throw ApiException.InvalidInput("Invalid development restart request.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        session = await db.DevSessions.SingleAsync(item => item.Id == sessionId, ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(item => item.PrincipalUserId == user.Id &&
            item.Route == Route && item.Target == sessionId.ToString() && item.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
            var prior = await db.DevSessionRuns.SingleAsync(item => item.OperationId == previous.OutcomeOperationId, ct);
            await tx.CommitAsync(ct);
            return Results.Json(Accepted(session.Id, prior.OperationId, prior.Id), RingfourJson.Options, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        if (session.ExpiresAt <= now || session.State is not ("ready" or "running"))
            throw ApiException.Conflict("session_unavailable", "The development session is not ready for restart.");
        if (session.Generation != generation)
            throw ApiException.Conflict("stale_generation", "The requested generation is not the current reported generation.");
        var presence = await db.RobotPresences.SingleOrDefaultAsync(item => item.RobotId == robot.Id, ct);
        if (manager.Get(robot.Id) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now, config.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
            throw new ApiException(503, "robot_unavailable", "The robot is offline.");
        if (await db.ScriptExecutions.AnyAsync(item => item.RobotId == robot.Id && (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct))
            throw ApiException.Conflict("execution_conflict", "Another execution is active.");
        if (await db.DevSessionRuns.AnyAsync(item => item.RobotId == robot.Id && item.SessionId != sessionId && (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct))
            throw ApiException.Conflict("execution_conflict", "Another development execution is active.");
        var deployment = await db.MissionDeployments.SingleOrDefaultAsync(item => item.RobotId == robot.Id, ct);
        if (deployment?.CandidateRevisionId is not null || (deployment is not null && await db.DeploymentActions.AnyAsync(item => item.DeploymentId == deployment.Id && (item.State == "pending" || item.State == "starting"), ct)))
            throw ApiException.Conflict("deployment_in_progress", "A mission deployment or restart is in progress.");
        if (await db.DevSessionRuns.CountAsync(item => item.RobotId == robot.Id, ct) >= 100)
            throw new ApiException(503, "capacity_exceeded", "Development execution retention capacity reached.");
        var opId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { operation_id = opId, session_id = sessionId, robot_id = robot.Id, generation, execution_id = executionId }, RingfourJson.Options);
        db.Operations.Add(new Operation { Id = opId, RobotId = robot.Id, Kind = "dev.session.restart", PayloadVersion = 1, Payload = payload,
            State = OperationState.Queued, CreatedAt = now, UpdatedAt = now, DeadlineAt = now.AddSeconds(config.GetValue("Connectivity:OperationDeadlineSeconds", 60)), RequestedByUserId = user.Id });
        db.DevSessionRuns.Add(new DevSessionRun { Id = executionId, SessionId = sessionId, RobotId = robot.Id, ActorUserId = user.Id,
            OperationId = opId, Generation = generation, State = "queued", CreatedAt = now, UpdatedAt = now });
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord { Id = Guid.NewGuid(), PrincipalUserId = user.Id,
            Route = Route, Target = sessionId.ToString(), Key = key, RequestFingerprint = fingerprint, OutcomeOperationId = opId,
            CreatedAt = now, ExpiresAt = now.AddHours(config.GetValue("Idempotency:TtlHours", 24)) });
        session.LastOperationId = opId;
        session.LastExecutionId = executionId;
        session.State = "starting";
        session.LastError = null;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await OperationEndpoints.DispatchAsync(db, manager, robot.Id, opId, ct);
        return Results.Json(Accepted(sessionId, opId, executionId), RingfourJson.Options, statusCode: 202);
    }

    private static object Accepted(Guid session, Guid operation, Guid execution) => new { session_id = session, operation_id = operation, execution_id = execution };
}
