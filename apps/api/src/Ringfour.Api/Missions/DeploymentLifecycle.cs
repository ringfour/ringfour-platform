using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Missions;

// The action operation is a durable workflow record, never sent to the device.
// Device start and stop operations have separate stable IDs.
public static class DeploymentLifecycle
{
    public static void MapDeploymentLifecycle(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/deployments").RequireAuthorization();
        foreach (var kind in new[] { "start", "stop", "restart" })
        {
            var action = kind;
            group.MapPost($"/{{deploymentId:guid}}/{action}", (HttpContext context, RingfourDbContext db,
                DeviceConnectionManager manager, IConfiguration config, Guid deploymentId, CancellationToken ct) =>
                Submit(context, db, manager, config, deploymentId, action, ct));
        }
    }

    private static async Task<IResult> Submit(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration config, Guid id, string kind, CancellationToken ct)
    {
        var (user, deployment) = await DeploymentEndpoints.RequireDeployment(context, db, id,
            kind == "stop" ? OrgRole.Operator : OrgRole.Developer, ct);
        var key = DeploymentEndpoints.Key(context);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {deployment.RobotId} FOR UPDATE", ct);
        await db.Entry(deployment).ReloadAsync(ct);
        var previous = await db.DeploymentActions.SingleOrDefaultAsync(item => item.DeploymentId == id &&
            item.ActorUserId == user.Id && item.Kind == kind && item.IdempotencyKey == key, ct);
        if (previous is not null)
        {
            await transaction.CommitAsync(ct);
            return Results.Json(new { deployment_id = id, operation_id = previous.Id, execution_id = previous.ExecutionId }, statusCode: 202);
        }
        if (deployment.SelectedRevisionId is null)
            throw ApiException.Conflict("conflict", "There is no selected mission revision.");
        if (deployment.CandidateRevisionId is not null)
            throw ApiException.Conflict("deployment_in_progress", "A candidate is being prepared.");
        var pending = await db.DeploymentActions.Include(item => item.Operation).SingleOrDefaultAsync(item =>
            item.DeploymentId == id && (item.State == "pending" || item.State == "starting"), ct);
        if (pending is not null && (kind != "stop" || pending.Kind == "stop"))
            throw ApiException.Conflict("deployment_in_progress", "A deployment action is already in progress.");
        var active = await db.ScriptExecutions.SingleOrDefaultAsync(item => item.RobotId == deployment.RobotId &&
            (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct);
        if (active is not null && (kind == "start" || active.DeploymentId != id))
            throw ApiException.Conflict("execution_conflict", "Another execution is active.");
        if (kind != "stop")
        {
            var presence = await db.RobotPresences.SingleOrDefaultAsync(item => item.RobotId == deployment.RobotId, ct);
            if (manager.Get(deployment.RobotId) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now,
                config.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
                throw new ApiException(503, "robot_unavailable", "The robot is offline.");
            if (await db.ScriptExecutions.CountAsync(item => item.RobotId == deployment.RobotId, ct) >= 100)
                throw new ApiException(503, "capacity_exceeded", "Execution retention capacity reached.");
        }
        // A stop cancels an unfinished start or restart under the same robot lock.
        // If restart already issued a stop, carry that stable operation forward.
        Guid? priorStop = null;
        if (pending is not null)
        {
            if (active is not null && pending.TargetExecutionId == active.Id) priorStop = pending.StopOperationId;
            pending.State = "failed";
            pending.UpdatedAt = now;
            pending.Operation.State = OperationState.Failed;
            pending.Operation.UpdatedAt = now;
            pending.Operation.Result = """{"error":{"code":"superseded","message":"The action was superseded by a stop."}}""";
        }
        var operationId = Guid.NewGuid();
        var executionId = kind == "stop" ? (Guid?)null : Guid.NewGuid();
        db.Operations.Add(new Operation
        {
            Id = operationId,
            RobotId = deployment.RobotId,
            Kind = "mission.lifecycle",
            PayloadVersion = 1,
            Payload = "{}",
            State = OperationState.Accepted,
            CreatedAt = now,
            UpdatedAt = now,
            DeadlineAt = now.AddYears(10),
            RequestedByUserId = user.Id
        });
        db.DeploymentActions.Add(new DeploymentAction
        {
            Id = operationId,
            DeploymentId = id,
            ActorUserId = user.Id,
            Kind = kind,
            State = "pending",
            IdempotencyKey = key,
            RequestFingerprint = kind,
            ExecutionId = executionId,
            TargetExecutionId = active?.Id,
            StopOperationId = priorStop,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24)
        });
        deployment.DesiredState = kind == "stop" ? "stopped" : "running";
        deployment.ObservedState = active is not null ? "stopping" : kind == "stop" ? "ready" : "starting";
        deployment.LastOperationId = operationId;
        deployment.LastError = null;
        deployment.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(new { deployment_id = id, operation_id = operationId, execution_id = executionId }, statusCode: 202);
    }
}

public sealed class DeploymentLifecycleWorker(IServiceScopeFactory scopes, DeviceConnectionManager manager,
    IConfiguration config, ILogger<DeploymentLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
                var ids = await db.DeploymentActions.AsNoTracking().Where(item => item.State == "pending" || item.State == "starting")
                    .OrderBy(item => item.CreatedAt).Select(item => item.Id).ToListAsync(stoppingToken);
                foreach (var id in ids) await Reconcile(db, id, stoppingToken);
                await RefreshObserved(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "mission lifecycle reconciliation failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task Reconcile(RingfourDbContext db, Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var robotId = await db.DeploymentActions.Where(item => item.Id == id).Select(item => item.Deployment.RobotId).SingleAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robotId} FOR UPDATE", ct);
        var action = await db.DeploymentActions.Include(item => item.Deployment).Include(item => item.Operation).SingleAsync(item => item.Id == id, ct);
        if (action.State is not ("pending" or "starting")) { await tx.CommitAsync(ct); return; }
        var deployment = action.Deployment;
        var now = DateTimeOffset.UtcNow;
        Guid? dispatch = null;
        if (action.State == "pending" && action.TargetExecutionId is Guid targetId)
        {
            var target = await db.ScriptExecutions.SingleAsync(item => item.Id == targetId, ct);
            if (!ScriptRunEndpoints.Terminal(target.State))
            {
                // A queued start must resolve before sending stop, otherwise stop can race ahead of start.
                var start = await db.Operations.SingleAsync(item => item.Id == targetId, ct);
                if (start.State == OperationState.Succeeded && action.StopOperationId is null)
                {
                    var stopId = Guid.NewGuid();
                    db.Operations.Add(new Operation
                    {
                        Id = stopId,
                        RobotId = robotId,
                        Kind = "script.execution.stop",
                        PayloadVersion = 1,
                        Payload = JsonSerializer.Serialize(new { execution_id = targetId }, RingfourJson.Options),
                        State = OperationState.Queued,
                        CreatedAt = now,
                        UpdatedAt = now,
                        DeadlineAt = now.AddYears(10),
                        RequestedByUserId = action.ActorUserId
                    });
                    action.StopOperationId = stopId;
                    dispatch = stopId;
                }
                if (action.StopOperationId is Guid stopOperationId)
                {
                    var stop = await db.Operations.SingleOrDefaultAsync(item => item.Id == stopOperationId, ct);
                    if (stop?.State == OperationState.Failed) Fail(action, deployment, now, "stop_failed", "The device could not stop the execution.");
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                if (dispatch is Guid stopDispatch) await OperationEndpoints.DispatchAsync(db, manager, robotId, stopDispatch, ct);
                return;
            }
        }
        if (action.State == "pending" && action.Kind == "stop")
        {
            Complete(action, now);
            deployment.ObservedState = "ready";
        }
        else if (action.State == "pending")
        {
            if (manager.Get(robotId) is null) { await tx.CommitAsync(ct); return; }
            if (await db.ScriptExecutions.AnyAsync(item => item.RobotId == robotId &&
                (item.State == "queued" || item.State == "preparing" || item.State == "running"), ct))
            { Fail(action, deployment, now, "execution_conflict", "Another execution is active."); }
            else
            {
                var source = await db.MissionRevisions.Where(item => item.Id == deployment.SelectedRevisionId).Select(item => item.ScriptRevision).SingleAsync(ct);
                var executionId = action.ExecutionId!.Value;
                db.Operations.Add(new Operation
                {
                    Id = executionId,
                    RobotId = robotId,
                    Kind = "script.execution.start",
                    PayloadVersion = 1,
                    Payload = JsonSerializer.Serialize(new
                    {
                        revision_id = source.Id,
                        archive_sha256 = source.ArchiveSha256,
                        archive_size = source.ArchiveSize,
                        revision_digest = source.RevisionDigest,
                        entrypoint = source.Entrypoint,
                        python_requirement = source.PythonRequirement,
                        lock_sha256 = source.LockSha256
                    }, RingfourJson.Options),
                    State = OperationState.Queued,
                    CreatedAt = now,
                    UpdatedAt = now,
                    DeadlineAt = now.AddSeconds(config.GetValue("Connectivity:OperationDeadlineSeconds", 60)),
                    RequestedByUserId = action.ActorUserId
                });
                db.ScriptExecutions.Add(new ScriptExecution
                {
                    Id = executionId,
                    RobotId = robotId,
                    RevisionId = source.Id,
                    DeploymentId = deployment.Id,
                    MissionRevisionId = deployment.SelectedRevisionId,
                    ActorUserId = action.ActorUserId,
                    State = "queued",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.ScriptExecutionEvents.Add(new ScriptExecutionEvent
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = executionId,
                    ActorUserId = action.ActorUserId,
                    State = "queued",
                    OccurredAt = now
                });
                action.State = "starting";
                deployment.LastExecutionId = executionId;
                deployment.ObservedState = "starting";
                dispatch = executionId;
            }
        }
        else
        {
            var execution = await db.ScriptExecutions.SingleAsync(item => item.Id == action.ExecutionId, ct);
            if (execution.State == "failed" && execution.Error is not null) Fail(action, deployment, now, "start_failed", "The execution failed to start or run.");
            else if (execution.State != "queued") Complete(action, now);
        }
        action.UpdatedAt = now;
        deployment.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (dispatch is Guid operationId) await OperationEndpoints.DispatchAsync(db, manager, robotId, operationId, ct);
    }

    private static void Complete(DeploymentAction action, DateTimeOffset now)
    {
        action.State = "completed";
        action.Operation.State = OperationState.Succeeded;
        action.Operation.UpdatedAt = now;
        action.Operation.Result = "{}";
    }

    private static void Fail(DeploymentAction action, MissionDeployment deployment, DateTimeOffset now, string code, string message)
    {
        action.State = "failed";
        action.Operation.State = OperationState.Failed;
        action.Operation.UpdatedAt = now;
        action.Operation.Result = JsonSerializer.Serialize(new { error = new { code, message } }, RingfourJson.Options);
        deployment.LastError = JsonSerializer.Serialize(new { code, message }, RingfourJson.Options);
        deployment.ObservedState = "failed";
    }

    private static async Task RefreshObserved(RingfourDbContext db, CancellationToken ct)
    {
        // Conditional updates cannot overwrite a newly submitted action or candidate.
        db.ChangeTracker.Clear();
        var snapshots = await db.MissionDeployments.AsNoTracking().Where(item => item.LastExecutionId != null)
            .Select(item => new { item.Id, ExecutionId = item.LastExecutionId!.Value }).ToListAsync(ct);
        foreach (var snapshot in snapshots)
        {
            var execution = await db.ScriptExecutions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshot.ExecutionId, ct);
            if (execution is null) continue;
            var observed = execution.State switch { "queued" or "preparing" => "starting", _ => execution.State };
            var now = DateTimeOffset.UtcNow;
            await db.MissionDeployments.Where(item => item.Id == snapshot.Id && item.LastExecutionId == snapshot.ExecutionId &&
                item.CandidateRevisionId == null && item.ObservedState != observed &&
                !db.DeploymentActions.Any(action => action.DeploymentId == item.Id && (action.State == "pending" || action.State == "starting")))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ObservedState, observed)
                    .SetProperty(item => item.UpdatedAt, now), ct);
        }
    }
}
