using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Storage;

public static class ScriptRunEndpoints
{
    private const string StartRoute = "POST /v1/robots/{robot_id}/script-runs";
    private const string StopRoute = "POST /v1/executions/{execution_id}/stop";

    public static void MapScriptRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();
        group.MapPost("/robots/{robotId:guid}/script-runs", Start);
        group.MapGet("/executions/{executionId:guid}", Get);
        group.MapGet("/executions/{executionId:guid}/output", GetOutput);
        group.MapGet("/executions/{executionId:guid}/artifacts", ListArtifacts);
        group.MapGet("/executions/{executionId:guid}/artifacts/content", DownloadArtifact);
        group.MapPost("/executions/{executionId:guid}/stop", Stop);
    }

    internal static bool Terminal(string state) => state is "succeeded" or "failed" or "cancelled";
    internal static string ArtifactKey(Guid executionId, string path) =>
        $"artifacts/{executionId:N}/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))}";

    private static string IdempotencyKey(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw ApiException.InvalidInput("Idempotency-Key is required and must not exceed 200 characters.");
        return key;
    }

    private static async Task<ScriptExecution> RequireExecution(HttpContext context, RingfourDbContext db, Guid id, OrgRole role, CancellationToken ct)
    {
        var execution = await db.ScriptExecutions.SingleOrDefaultAsync(candidate => candidate.Id == id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        await RobotAccess.RequireRobotAccess(context, db, execution.RobotId, role, ct);
        return execution;
    }

    private static async Task<IResult> Start(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration configuration, Guid robotId, CancellationToken ct)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, robotId, OrgRole.Developer, ct);
        var key = IdempotencyKey(context);
        JsonObject? document;
        try { document = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject; }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid run request."); }
        if (document is null || document.Count != 1 || document["revision_id"] is not JsonValue value ||
            !value.TryGetValue<string>(out var revisionText) || !Guid.TryParseExact(revisionText, "D", out var revisionId) ||
            revisionId.ToString() != revisionText)
            throw ApiException.InvalidInput("Invalid run request.");
        var revision = await db.ScriptRevisions.SingleOrDefaultAsync(candidate => candidate.Id == revisionId && candidate.OrganizationId == robot.OrganizationId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        if (revision.State != "ready") throw ApiException.Conflict("conflict", "Revision is not finalized.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revisionId.ToString())));
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robotId} FOR UPDATE", ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(candidate => candidate.PrincipalUserId == user.Id &&
            candidate.Route == StartRoute && candidate.Target == robotId.ToString() && candidate.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
            await transaction.CommitAsync(ct);
            return Results.Json(new { operation_id = previous.OutcomeOperationId, execution_id = previous.OutcomeOperationId }, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        var presence = await db.RobotPresences.SingleOrDefaultAsync(candidate => candidate.RobotId == robotId, ct);
        if (manager.Get(robotId) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now,
            configuration.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
            throw new ApiException(503, "robot_unavailable", "The robot is offline.");
        if (await db.ScriptExecutions.AnyAsync(candidate => candidate.RobotId == robotId &&
            (candidate.State == "queued" || candidate.State == "preparing" || candidate.State == "running"), ct))
            throw ApiException.Conflict("execution_conflict", "Another execution is active.");
        if (await db.ScriptExecutions.CountAsync(candidate => candidate.RobotId == robotId, ct) >= 100)
            throw new ApiException(503, "capacity_exceeded", "Execution retention capacity reached.");
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            revision_id = revision.Id,
            archive_sha256 = revision.ArchiveSha256,
            archive_size = revision.ArchiveSize,
            revision_digest = revision.RevisionDigest,
            entrypoint = revision.Entrypoint,
            python_requirement = revision.PythonRequirement,
            lock_sha256 = revision.LockSha256
        }, RingfourJson.Options);
        var operation = new Operation
        {
            Id = id,
            RobotId = robotId,
            Kind = "script.execution.start",
            PayloadVersion = 1,
            Payload = payload,
            State = OperationState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            DeadlineAt = now.AddSeconds(configuration.GetValue("Connectivity:OperationDeadlineSeconds", 60)),
            RequestedByUserId = user.Id
        };
        db.Operations.Add(operation);
        db.ScriptExecutions.Add(new ScriptExecution
        {
            Id = id,
            RobotId = robotId,
            RevisionId = revision.Id,
            ActorUserId = user.Id,
            State = "queued",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.ScriptExecutionEvents.Add(new ScriptExecutionEvent
        {
            Id = Guid.NewGuid(),
            ExecutionId = id,
            ActorUserId = user.Id,
            State = "queued",
            OccurredAt = now
        });
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            PrincipalUserId = user.Id,
            Route = StartRoute,
            Target = robotId.ToString(),
            Key = key,
            RequestFingerprint = fingerprint,
            OutcomeOperationId = id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(configuration.GetValue("Idempotency:TtlHours", 24))
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await OperationEndpoints.DispatchAsync(db, manager, robotId, id, ct);
        return Results.Json(new { operation_id = id, execution_id = id }, statusCode: 202);
    }

    private static async Task<IResult> Get(HttpContext context, RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        var execution = await RequireExecution(context, db, executionId, OrgRole.Operator, ct);
        return Results.Ok(new
        {
            id = execution.Id,
            robot_id = execution.RobotId,
            revision_id = execution.RevisionId,
            actor_user_id = execution.ActorUserId,
            state = execution.State,
            created_at = execution.CreatedAt,
            updated_at = execution.UpdatedAt,
            exit_code = execution.ExitCode,
            error = execution.Error is null ? null : JsonNode.Parse(execution.Error)
        });
    }

    private static async Task<IResult> GetOutput(HttpContext context, RingfourDbContext db, Guid executionId,
        string stream, int offset, int limit, CancellationToken ct)
    {
        var execution = await RequireExecution(context, db, executionId, OrgRole.Operator, ct);
        if (stream is not ("stdout" or "stderr") || offset is < 0 or > 65536 || limit is < 1 or > 16384)
            throw ApiException.InvalidInput("Invalid output range or stream.");
        var bytes = stream == "stdout" ? execution.Stdout : execution.Stderr;
        var count = Math.Min(limit, Math.Max(0, bytes.Length - offset));
        var data = count == 0 ? [] : bytes.AsSpan(offset, count).ToArray();
        var next = offset + count;
        return Results.Ok(new
        {
            execution_id = executionId,
            stream,
            offset,
            next_offset = next,
            data_base64 = Convert.ToBase64String(data),
            eof = Terminal(execution.State) && next >= bytes.Length,
            truncated = stream == "stdout" ? execution.StdoutTruncated : execution.StderrTruncated
        });
    }

    private static async Task<IResult> ListArtifacts(HttpContext context, RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        await RequireExecution(context, db, executionId, OrgRole.Operator, ct);
        var artifacts = await db.ScriptArtifacts.Where(candidate => candidate.ExecutionId == executionId).OrderBy(candidate => candidate.Path)
            .Select(candidate => new { path = candidate.Path, size = candidate.Size, sha256 = candidate.Sha256 }).ToArrayAsync(ct);
        return Results.Ok(new { execution_id = executionId, artifacts });
    }

    private static async Task<IResult> DownloadArtifact(HttpContext context, RingfourDbContext db, IObjectStore store,
        Guid executionId, string path, CancellationToken ct)
    {
        await RequireExecution(context, db, executionId, OrgRole.Operator, ct);
        var artifact = await db.ScriptArtifacts.SingleOrDefaultAsync(candidate => candidate.ExecutionId == executionId && candidate.Path == path, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        byte[] data;
        try { data = await store.GetAsync(ArtifactKey(executionId, path), ct); }
        catch (Exception exception) when (exception is IOException or Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
        if (data.Length != artifact.Size || Convert.ToHexStringLower(SHA256.HashData(data)) != artifact.Sha256)
            throw new ApiException(503, "storage_unavailable", "The artifact store returned invalid bytes.");
        return Results.File(data, "application/octet-stream", Path.GetFileName(path));
    }

    private static async Task<IResult> Stop(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration configuration, Guid executionId, CancellationToken ct)
    {
        var execution = await RequireExecution(context, db, executionId, OrgRole.Operator, ct);
        var (user, _) = await RobotAccess.RequireRobotAccess(context, db, execution.RobotId, OrgRole.Operator, ct);
        var key = IdempotencyKey(context);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {execution.RobotId} FOR UPDATE", ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(candidate => candidate.PrincipalUserId == user.Id &&
            candidate.Route == StopRoute && candidate.Target == executionId.ToString() && candidate.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            await transaction.CommitAsync(ct);
            return Results.Json(new { operation_id = previous.OutcomeOperationId, execution_id = executionId }, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        await db.Entry(execution).ReloadAsync(ct);
        if (Terminal(execution.State)) throw ApiException.Conflict("conflict", "Execution is already terminal.");
        var presence = await db.RobotPresences.SingleOrDefaultAsync(candidate => candidate.RobotId == execution.RobotId, ct);
        if (manager.Get(execution.RobotId) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now,
            configuration.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
            throw new ApiException(503, "robot_unavailable", "The robot is offline.");
        var id = Guid.NewGuid();
        db.Operations.Add(new Operation
        {
            Id = id,
            RobotId = execution.RobotId,
            Kind = "script.execution.stop",
            PayloadVersion = 1,
            Payload = JsonSerializer.Serialize(new { execution_id = executionId }, RingfourJson.Options),
            State = OperationState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            DeadlineAt = now.AddSeconds(configuration.GetValue("Connectivity:OperationDeadlineSeconds", 60)),
            RequestedByUserId = user.Id
        });
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            PrincipalUserId = user.Id,
            Route = StopRoute,
            Target = executionId.ToString(),
            Key = key,
            RequestFingerprint = "stop",
            OutcomeOperationId = id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(configuration.GetValue("Idempotency:TtlHours", 24))
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await OperationEndpoints.DispatchAsync(db, manager, execution.RobotId, id, ct);
        return Results.Json(new { operation_id = id, execution_id = executionId }, statusCode: 202);
    }
}
