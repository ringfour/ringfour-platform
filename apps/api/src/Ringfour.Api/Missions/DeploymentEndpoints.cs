using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Missions;

public static class DeploymentEndpoints
{
    private const string DeployRoute = "POST /v1/robots/{robot_id}/deployments";

    public static void MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var human = endpoints.MapGroup("/v1").RequireAuthorization();
        human.MapPost("/robots/{robotId:guid}/deployments", Deploy);
        human.MapGet("/deployments/{deploymentId:guid}", Get);
        endpoints.MapGet("/v1/device/deployments/{deploymentId:guid}/candidate", Candidate);
    }

    internal static string Key(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw ApiException.InvalidInput("Idempotency-Key is required and must not exceed 200 characters.");
        return key;
    }

    internal static async Task<(User User, MissionDeployment Deployment)> RequireDeployment(HttpContext context,
        RingfourDbContext db, Guid id, OrgRole role, CancellationToken ct)
    {
        var deployment = await db.MissionDeployments.SingleOrDefaultAsync(item => item.Id == id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var (user, _) = await RobotAccess.RequireRobotAccess(context, db, deployment.RobotId, role, ct);
        return (user, deployment);
    }

    private static async Task<IResult> Deploy(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration configuration, Guid robotId, CancellationToken ct)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, robotId, OrgRole.Developer, ct);
        var key = Key(context);
        JsonObject? body;
        try { body = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject; }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid deployment request."); }
        if (body is null || body.Count != 1 || body["mission_revision_id"] is not JsonValue value ||
            !value.TryGetValue<string>(out var text) || !Guid.TryParseExact(text, "D", out var revisionId) || revisionId.ToString() != text)
            throw ApiException.InvalidInput("Invalid deployment request.");
        var revision = await db.MissionRevisions.Include(item => item.Mission).Include(item => item.ScriptRevision)
            .SingleOrDefaultAsync(item => item.Id == revisionId && item.Mission.OrganizationId == robot.OrganizationId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revisionId.ToString())));
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robotId} FOR UPDATE", ct);
        var deployment = await db.MissionDeployments.SingleOrDefaultAsync(item => item.RobotId == robotId, ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(item => item.PrincipalUserId == user.Id &&
            item.Route == DeployRoute && item.Target == robotId.ToString() && item.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
            if (deployment is null) throw new InvalidOperationException("Deployment idempotency record has no deployment.");
            await transaction.CommitAsync(ct);
            return Results.Json(new { deployment_id = deployment.Id, operation_id = previous.OutcomeOperationId }, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        var presence = await db.RobotPresences.SingleOrDefaultAsync(item => item.RobotId == robotId, ct);
        if (manager.Get(robotId) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now,
            configuration.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
            throw new ApiException(503, "robot_unavailable", "The robot is offline.");
        if (deployment?.CandidateRevisionId is not null || (deployment is not null && await db.DeploymentActions.AnyAsync(
            item => item.DeploymentId == deployment.Id && (item.State == "pending" || item.State == "starting"), ct)))
            throw ApiException.Conflict("deployment_in_progress", "A deployment or restart is already in progress.");
        if (await db.ScriptExecutions.AnyAsync(item => item.RobotId == robotId &&
            (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct))
            throw ApiException.Conflict("execution_conflict", "An execution is active; stop it before deploying.");
        var retain = new List<string>();
        if (deployment?.SelectedRevisionId is Guid selectedId)
        {
            var selectedDigest = await db.MissionRevisions.Where(item => item.Id == selectedId).Select(item => item.ScriptRevision.RevisionDigest).SingleAsync(ct);
            retain.Add(selectedDigest);
        }
        var id = Guid.NewGuid();
        var source = revision.ScriptRevision;
        var payload = JsonSerializer.Serialize(new
        {
            deployment_id = deployment?.Id ?? Guid.Empty,
            mission_revision_id = revisionId,
            revision_id = source.Id,
            archive_sha256 = source.ArchiveSha256,
            archive_size = source.ArchiveSize,
            revision_digest = source.RevisionDigest,
            entrypoint = source.Entrypoint,
            python_requirement = source.PythonRequirement,
            lock_sha256 = source.LockSha256,
            retain_digests = retain
        }, RingfourJson.Options);
        if (deployment is null)
        {
            deployment = new MissionDeployment
            {
                Id = Guid.NewGuid(),
                RobotId = robotId,
                DesiredState = "stopped",
                ObservedState = "preparing",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.MissionDeployments.Add(deployment);
            payload = JsonSerializer.Serialize(new
            {
                deployment_id = deployment.Id,
                mission_revision_id = revisionId,
                revision_id = source.Id,
                archive_sha256 = source.ArchiveSha256,
                archive_size = source.ArchiveSize,
                revision_digest = source.RevisionDigest,
                entrypoint = source.Entrypoint,
                python_requirement = source.PythonRequirement,
                lock_sha256 = source.LockSha256,
                retain_digests = retain
            }, RingfourJson.Options);
        }
        deployment.CandidateRevisionId = revisionId;
        deployment.CandidateOperationId = id;
        deployment.DesiredState = "stopped";
        deployment.ObservedState = "preparing";
        deployment.LastOperationId = id;
        deployment.LastError = null;
        deployment.UpdatedAt = now;
        db.Operations.Add(new Operation
        {
            Id = id,
            RobotId = robotId,
            Kind = "mission.prepare",
            PayloadVersion = 1,
            Payload = payload,
            State = OperationState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            DeadlineAt = now.AddSeconds(configuration.GetValue("Missions:PrepareTimeoutSeconds", 600)),
            RequestedByUserId = user.Id
        });
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            PrincipalUserId = user.Id,
            Route = DeployRoute,
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
        return Results.Json(new { deployment_id = deployment.Id, operation_id = id }, statusCode: 202);
    }

    private static async Task<IResult> Get(HttpContext context, RingfourDbContext db, Guid deploymentId, CancellationToken ct)
    {
        var (_, item) = await RequireDeployment(context, db, deploymentId, OrgRole.Operator, ct);
        return Results.Ok(new
        {
            id = item.Id,
            robot_id = item.RobotId,
            selected_revision_id = item.SelectedRevisionId,
            candidate_revision_id = item.CandidateRevisionId,
            desired_state = item.DesiredState,
            observed_state = item.ObservedState,
            last_operation_id = item.LastOperationId,
            last_execution_id = item.LastExecutionId,
            last_error = item.LastError is null ? null : JsonNode.Parse(item.LastError),
            created_at = item.CreatedAt,
            updated_at = item.UpdatedAt
        });
    }

    private static async Task<IResult> Candidate(HttpContext context, RingfourDbContext db, IObjectStore store,
        Guid deploymentId, [FromQuery(Name = "operation_id")] Guid operationId, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var secret = context.Request.Headers["x-ringfour-credential-secret"].ToString();
        if (string.IsNullOrWhiteSpace(secret)) throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, _) = await DeviceAuth.VerifyCredential(db, credentialId, secret, ct);
        var deployment = await db.MissionDeployments.SingleOrDefaultAsync(item => item.Id == deploymentId && item.RobotId == robot.Id && item.CandidateOperationId == operationId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist for this robot.");
        var source = await db.MissionRevisions.Where(item => item.Id == deployment.CandidateRevisionId)
            .Select(item => item.ScriptRevision).SingleAsync(ct);
        byte[] archive;
        try { archive = await store.GetAsync($"revisions/{source.Id:N}", ct); }
        catch (Exception exception) when (exception is IOException or AmazonServiceException or AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
        if (archive.Length != source.ArchiveSize || Convert.ToHexStringLower(SHA256.HashData(archive)) != source.ArchiveSha256)
            throw new ApiException(503, "storage_unavailable", "The artifact store returned invalid bytes.");
        return Results.File(archive, "application/zip");
    }
}
