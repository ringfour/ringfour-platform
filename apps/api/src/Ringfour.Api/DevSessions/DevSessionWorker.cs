using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.DevSessions;

// The durable operation is authoritative; this worker only projects its result
// and schedules expiry or cleanup retries. It never starts user code.
public sealed class DevSessionWorker(IServiceScopeFactory scopes, DeviceConnectionManager manager, ILogger<DevSessionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
                var ids = await db.DevSessions.AsNoTracking().Where(item => (item.State != "closed" && item.State != "expired") ||
                    db.DevSessionRuns.Any(run => run.SessionId == item.Id && (run.State == "queued" || run.State == "preparing" || run.State == "running")))
                    .Select(item => item.Id).ToListAsync(stoppingToken);
                foreach (var id in ids) await Reconcile(db, id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "development session reconciliation failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task Reconcile(RingfourDbContext db, Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var robotId = await db.DevSessions.Where(item => item.Id == id).Select(item => item.RobotId).SingleAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robotId} FOR UPDATE", ct);
        var item = await db.DevSessions.SingleAsync(item => item.Id == id, ct);
        var now = DateTimeOffset.UtcNow;
        var op = item.LastOperationId is Guid operationId
            ? await db.Operations.SingleOrDefaultAsync(operation => operation.Id == operationId, ct) : null;
        if (op is not null && OperationStates.IsTerminal(op.State) && item.State is "creating" or "closing")
        {
            if (op.State == OperationState.Succeeded && ValidResult(op, item))
            {
                item.State = op.Kind switch { "dev.session.create" => "ready", "dev.session.expire" => "expired", _ => "closed" };
                item.ClosedAt = item.State is "closed" or "expired" ? now : null;
                item.LastError = null;
                item.UpdatedAt = now;
            }
            else if (item.State == "creating")
            {
                item.State = "failed";
                item.LastError = """{"code":"session_create_failed","message":"The device could not create the development workspace."}""";
                item.UpdatedAt = now;
            }
            else if (item.LastError is null)
            {
                item.LastError = """{"code":"session_cleanup_failed","message":"Workspace cleanup will be retried."}""";
                item.UpdatedAt = now;
            }
        }
        // A definitively rejected/expired restart cannot leave a queued run forever.
        var failedRuns = await db.DevSessionRuns.Include(run => run.Operation).Where(run => run.SessionId == item.Id &&
            (run.State == "queued" || run.State == "preparing") &&
            (run.Operation.State == OperationState.Failed || run.Operation.State == OperationState.Cancelled)).ToListAsync(ct);
        foreach (var run in failedRuns)
        {
            run.State = "failed";
            run.Error = """{"code":"restart_failed","message":"The device could not start the development execution."}""";
            run.UpdatedAt = now;
            if (item.LastExecutionId == run.Id && item.State is not ("closing" or "closed" or "expired"))
            {
                var otherActive = await db.DevSessionRuns.AnyAsync(other => other.SessionId == item.Id && other.Id != run.Id &&
                    (other.State == "queued" || other.State == "preparing" || other.State == "running"), ct);
                item.State = otherActive ? "running" : "ready";
                item.LastError = run.Error;
                item.UpdatedAt = now;
            }
        }
        Guid? dispatch = null;
        if (item.ExpiresAt <= now && item.State is not ("closing" or "closed" or "expired"))
        {
            var expire = DevSessionEndpoints.NewLifecycle(item, "expire", now, null);
            db.Operations.Add(expire);
            item.LastOperationId = expire.Id;
            item.State = "closing";
            item.UpdatedAt = now;
            dispatch = expire.Id;
        }
        else if (item.State == "closing" && op is not null && OperationStates.IsTerminal(op.State) &&
                 item.LastError is not null && item.UpdatedAt <= now.AddSeconds(-30))
        {
            var retry = DevSessionEndpoints.NewLifecycle(item, op.Kind == "dev.session.expire" ? "expire" : "close", now, op.RequestedByUserId);
            db.Operations.Add(retry);
            item.LastOperationId = retry.Id;
            item.UpdatedAt = now;
            dispatch = retry.Id;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (dispatch is Guid next) await OperationEndpoints.DispatchAsync(db, manager, robotId, next, ct);
    }

    private static bool ValidResult(Operation op, DevSession session)
    {
        try
        {
            var result = JsonNode.Parse(op.Result ?? "{}");
            var expected = op.Kind == "dev.session.create" ? "ready" : "closed";
            return result?["session_id"]?.GetValue<string>() == session.Id.ToString() &&
                result?["state"]?.GetValue<string>() == expected && result?["generation"]?.GetValue<int>() == 0;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return false; }
    }
}
