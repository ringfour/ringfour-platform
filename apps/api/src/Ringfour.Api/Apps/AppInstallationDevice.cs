using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

public static class AppInstallationDevice
{
    public static void MapAppInstallationDevice(this IEndpointRouteBuilder routes)
    {
        routes.MapPut("/v1/device/app-observations", Observe).WithMetadata(new AppErrorResponse());
        routes.MapGet("/v1/device/app-revisions/{revisionId:guid}", Revision).WithMetadata(new AppErrorResponse());
    }

    internal static async Task<RobotAppInventory> Session(HttpContext c, RingfourDbContext db, DeviceConnectionManager manager, Guid robot, CancellationToken ct)
    {
        if (!Guid.TryParse(c.Request.Headers["x-ringfour-device-session"], out var session))
            throw ApiException.Conflict("session_replaced", "An active negotiated session is required.");
        var current = manager.Get(robot);
        if (current is null || current.SessionId != session || current.Socket.State != System.Net.WebSockets.WebSocketState.Open)
            throw ApiException.Conflict("session_replaced", "Device session was replaced.");
        return await db.RobotAppInventories.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot && x.SessionId == session && x.Supported && x.InstallationsSupported, ct)
            ?? throw ApiException.Conflict("daemon_upgrade_required", "Negotiate installation support first.");
    }

    private static async Task<IResult> Revision(Guid revisionId, [FromQuery(Name="operation_id")] Guid operationId, HttpContext c, RingfourDbContext db, DeviceConnectionManager manager, CancellationToken ct)
    {
        c.Response.Headers.CacheControl = "no-store";
        var robot = await AppInventoryEndpoints.Authenticate(c, db, ct);
        var fence = manager.AppSessionFence(robot.Id); await fence.WaitAsync(ct);
        try
        {
            await AppInventoryEndpoints.Authenticate(c, db, ct);
            await Session(c, db, manager, robot.Id, ct);
            var operation = await db.AppOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId && x.RobotId == robot.Id && AppInstallationEndpoints.Pending.Contains(x.State), ct);
            if (operation?.Command is null || (JsonNode.Parse(operation.Command)!["release"] ?? JsonNode.Parse(operation.Command)!["snapshot"])!["revision_id"]!.GetValue<string>() != revisionId.ToString())
                throw ApiException.NotFound("No authorized preparation operation.");
            if (operation.DeadlineAt <= DateTimeOffset.UtcNow) throw ApiException.Conflict("deadline_exceeded", "Operation expired.");
            var revision = await db.AppRevisions.AsNoTracking().SingleAsync(x => x.Id == revisionId, ct);
            return Results.Json(AppCatalogEndpoints.RevisionJson(revision, includeImages: false));
        }
        finally { fence.Release(); }
    }

    internal static async Task<bool> Ack(RingfourDbContext db, DeviceConnectionManager manager, DeviceConnection session, Guid id, CancellationToken ct)
    {
        if (!await db.AppOperations.AnyAsync(x => x.Id == id && x.RobotId == session.RobotId, ct)) return false;
        var fence = manager.AppSessionFence(session.RobotId); await fence.WaitAsync(ct);
        try
        {
            if (manager.Get(session.RobotId)?.SessionId != session.SessionId) return true;
            var inventory = await db.RobotAppInventories.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == session.RobotId && x.SessionId == session.SessionId, ct);
            if (inventory is null || !inventory.InstallationsSupported || (await db.AppOperations.AsNoTracking().Where(x => x.Id == id).Select(x => x.Kind).SingleAsync(ct) == "uninstall" && !inventory.UninstallSupported)) return true;
            var now = DateTimeOffset.UtcNow;
            await db.AppOperations.Where(x => x.Id == id && x.RobotId == session.RobotId && x.State == "queued")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, "accepted").SetProperty(x => x.Phase, "accepted").SetProperty(x => x.AcknowledgedAt, now), ct);
            await db.AppSettingsApplications.Where(x => x.ApplicationId == id && x.State == "pending" && x.DeliveryState == "queued")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.DeliveryState, "delivered").SetProperty(x => x.DeliveredAt, now), ct);
            return true;
        }
        finally { fence.Release(); }
    }

    private static ApiException Invalid() => ApiException.Conflict("invalid_observation", "Observation does not match durable installation intent.");
    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a,b);
    private static void Match(JsonNode binding, JsonNode release, Guid operation, bool installed)
    {
        if (!Same(binding["revision_id"],release["revision_id"]) || !Same(binding["configuration_revision_id"],release["configuration_revision_id"]) ||
            !Same(binding["image"],release["target"]!["image"]) || binding[installed ? "applied_operation_id" : "operation_id"]?.GetValue<string>() != operation.ToString()) throw Invalid();
    }

    private static async Task<IResult> Observe(HttpContext c, RingfourDbContext db, DeviceConnectionManager manager, CancellationToken ct)
    {
        var robot = await AppInventoryEndpoints.Authenticate(c, db, ct);
        var body = await AppProtocol.Read(c.Request, "observations", ct);
        var id = Guid.Parse(body["report_id"]!.GetValue<string>()); var sequence = AppProtocol.Integer(body["report_sequence"]!); var digest = AppProtocol.Digest(body);
        var fence = manager.AppSessionFence(robot.Id); await fence.WaitAsync(ct);
        try
        {
            await AppInventoryEndpoints.Authenticate(c, db, ct);
            var session = await Session(c, db, manager, robot.Id, ct);
            if (body["deployment_namespace"]!.GetValue<string>() != session.DeploymentNamespace.ToString()) throw ApiException.Conflict("ownership_conflict", "Namespace mismatch.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            var previous = await db.AppDeviceReports.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot.Id && x.ReportId == id, ct);
            if (previous is not null)
            { if (previous.Route != "observations" || previous.BodySha256 != digest) throw ApiException.Conflict("conflicting_report", "Report ID content changed."); return Results.Json(JsonNode.Parse(previous.Receipt)); }
            var last = await db.AppDeviceReports.Where(x => x.RobotId == robot.Id && x.Route == "observations").Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;
            if (sequence != last + 1) throw ApiException.Conflict("stale_observation", "Observation sequence must advance by exactly one.");
            var ops = body["operations"]!.AsArray(); var installations = body["installations"]!.AsArray();
            if ((body["runs"]!.AsArray().Count == 0 && ops.Count != installations.Count) ||
                ops.Select(x => x!["operation_id"]!.ToString()).Distinct().Count() != ops.Count || installations.Select(x => x!["installation_id"]!.ToString()).Distinct().Count() != installations.Count) throw Invalid();
            var now = DateTimeOffset.UtcNow;
            if (body["runs"]!.AsArray().Count > 0 && !session.RunsSupported) throw ApiException.Conflict("daemon_upgrade_required", "Negotiate run support first.");
            if (ops.Any(x => x!["kind"]!.GetValue<string>() == "settings.apply") && (!session.ManifestV2Supported || !session.LiveSettingsSupported))
                throw ApiException.Conflict("daemon_upgrade_required", "Negotiate live-settings support first.");
            await AppRunDevice.Observe(db, robot.Id, body, now, ct);
            foreach (var item in ops.Where(x => x!["kind"]!.GetValue<string>() is "install" or "configure" or "update" or "rollback" or "uninstall"))
            {
                var operationId = Guid.Parse(item!["operation_id"]!.GetValue<string>());
                var o = await db.AppOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.RobotId == robot.Id, ct) ?? throw Invalid();
                if (o.Command is null || o.Kind is not ("install" or "configure" or "update" or "rollback" or "uninstall") || o.RunId is not null || item["run_id"] is not null || item["kind"]!.GetValue<string>() != o.Kind || item["installation_id"]!.GetValue<string>() != o.InstallationId.ToString()) throw Invalid();
                var observed = DateTimeOffset.Parse(item["observed_at"]!.GetValue<string>());
                var state = item["state"]!.GetValue<string>(); var phase = item["phase"]!.GetValue<string>();
                var incoming = installations.SingleOrDefault(x => x!["installation_id"]!.GetValue<string>() == o.InstallationId.ToString()) ?? throw Invalid();
                if (state == "succeeded" && o.Kind != "uninstall") Match(incoming["installed"] ?? throw Invalid(), JsonNode.Parse(o.Command)!["release"]!, operationId, true);
                if (!AppInstallationEndpoints.Pending.Contains(o.State))
                {
                    if (o.State != state || o.Phase != phase || o.ObservedAt != observed || !Same(o.Failure is null ? null : JsonNode.Parse(o.Failure), item["failure"])) throw Invalid();
                    continue; // No stale report can overwrite an intervening binding.
                }
                var phases = new[] { "queued", "accepted", "checking", "staging", "staged", "applied" };
                if (state == "cancelled" || (state != "failed" && Array.IndexOf(phases,phase) < Array.IndexOf(phases,o.Phase))) throw Invalid();
                var installation = await db.AppInstallations.SingleAsync(x => x.Id == o.InstallationId, ct);
                var candidate = installation.Candidate is null ? null : JsonNode.Parse(installation.Candidate)!.AsObject();
                if (o.Kind != "uninstall" && candidate?["operation_id"]!.GetValue<string>() != operationId.ToString()) throw Invalid();
                if (o.Kind == "uninstall" && candidate is not null) throw Invalid();
                var command = JsonNode.Parse(o.Command)!; var release = command["release"];
                var oldBinding = installation.InstalledBinding is null ? null : JsonNode.Parse(installation.InstalledBinding)!.AsObject();
                if (!Same(command["expected_revision_id"],oldBinding?["revision_id"]) || !Same(command["expected_configuration_revision_id"],oldBinding?["configuration_revision_id"])) throw Invalid();
                if (state == "succeeded")
                {
                    if (phase != "applied" || (o.Kind != "uninstall" && incoming["installed"] is null) || incoming["candidate"] is not null) throw Invalid();
                    await AppInstallationEndpoints.RequireIdle(db,robot.Id,ct);
                    if (o.Kind == "uninstall")
                    {
                        if (incoming["installed"] is not null || incoming["candidate"] is not null) throw Invalid();
                        installation.InstalledBinding = null; installation.Candidate = null;
                        installation.SelectedSettingsRevisionId = null; installation.RemovedAt = observed;
                    }
                    else
                    {
                        if (incoming["installed"] is null) throw Invalid();
                        Match(incoming["installed"]!,release!,operationId,true);
                        var binding = incoming["installed"]!.DeepClone().AsObject(); binding["observed_at"] = incoming["observed_at"]!.DeepClone();
                        installation.InstalledBinding = binding.ToJsonString(); installation.Candidate = null; installation.RemovedAt = null;
                        if (release!.AsObject().ContainsKey("runtime_settings_revision_id"))
                            installation.SelectedSettingsRevisionId = release["runtime_settings_revision_id"] is null ? null : Guid.Parse(release["runtime_settings_revision_id"]!.GetValue<string>());
                    }
                    db.LogbookEvents.Add(new LogbookEvent { Id=o.Id,RobotId=robot.Id,AppOperationId=o.Id,AppInstallationId=installation.Id,ActorUserId=o.RequestedByUserId,Type=o.Kind == "uninstall" ? "app.installation.removed" : "app.installation.changed",SchemaVersion=3,OccurredAt=observed,ReceivedAt=now,Payload=new JsonObject { ["kind"]=o.Kind, ["revision_id"]=(release?["revision_id"] ?? command["expected_revision_id"])?.DeepClone(), ["runtime_settings_revision_id"]=release?["runtime_settings_revision_id"]?.DeepClone() }.ToJsonString() });
                }
                else if (o.Kind == "uninstall")
                {
                    if (!Same(oldBinding, incoming["installed"]) || incoming["candidate"] is not null) throw Invalid();
                }
                else
                {
                    var releaseValue = release ?? throw Invalid();
                    var candidateValue = candidate ?? throw Invalid();
                    oldBinding?.Remove("observed_at");
                    if (!Same(oldBinding,incoming["installed"]) || incoming["candidate"] is null) throw Invalid();
                    var staged = incoming["candidate"]!; Match(staged,releaseValue,operationId,false);
                    if ((state == "failed") != (staged["state"]!.GetValue<string>() == "failed") || !Same(staged["failure"],item["failure"])) throw Invalid();
                    if ((phase == "staged") != (staged["state"]!.GetValue<string>() == "staged")) throw Invalid();
                    candidateValue["state"] = staged["state"]!.DeepClone(); candidateValue["observed_at"] = incoming["observed_at"]!.DeepClone();
                    candidateValue["staged_at"] = staged["staged_at"]?.DeepClone(); candidateValue["failure"] = staged["failure"]?.DeepClone();
                    installation.Candidate = candidateValue.ToJsonString();
                }
                installation.LastObservedAt = DateTimeOffset.Parse(incoming["observed_at"]!.GetValue<string>());
                o.State = state; o.Phase = phase; o.ObservedAt = observed; o.ReceivedAt = now; o.AcknowledgedAt ??= now;
                o.Failure = item["failure"]?.ToJsonString(); if (state is "succeeded" or "failed") o.CompletedAt = now;
            }
            var receipt = new JsonObject { ["report_id"] = id.ToString(), ["report_sequence"] = sequence, ["received_at"] = AppProtocol.Timestamp(now) };
            db.AppDeviceReports.Add(new AppDeviceReport { RobotId = robot.Id, DeploymentNamespace = session.DeploymentNamespace, ReportId = id, Route = "observations", Sequence = sequence, BodySha256 = digest, Receipt = receipt.ToJsonString() });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Json(receipt);
        }
        finally { fence.Release(); }
    }
}

// A bounded in-process wakeup; PostgreSQL remains the durable work queue.
// Notifications may coalesce or be lost on process exit: the periodic scan recovers them.
public sealed class AppDispatchSignal
{
    private readonly SemaphoreSlim pending = new(0, 1);
    public void Notify()
    {
        try { pending.Release(); }
        catch (SemaphoreFullException) { /* An earlier wakeup is already queued. */ }
    }
    public Task<bool> WaitAsync(TimeSpan fallback, CancellationToken cancellationToken) =>
        pending.WaitAsync(fallback, cancellationToken);
}

public sealed class AppInstallationWorker(IServiceScopeFactory scopes, DeviceConnectionManager manager, IConfiguration config, ILogger<AppInstallationWorker> logger, AppDispatchSignal signal) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue("Apps:InstallationsEnabled", false))
                {
                    using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
                    var ids = await db.AppOperations.AsNoTracking().Where(x => AppInstallationEndpoints.Pending.Contains(x.State) && x.Command != null).OrderBy(x => x.RequestedAt).Select(x => x.Id).ToListAsync(stoppingToken);
                    foreach (var id in ids) await Dispatch(db, id, manager, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error,"App installation reconciliation failed"); }
            try { await signal.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(10, config.GetValue("Apps:InstallationSweepMilliseconds", 2000))), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public static async Task Dispatch(RingfourDbContext db, Guid id, DeviceConnectionManager manager, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); var robot = await db.AppOperations.Where(x => x.Id == id).Select(x => x.RobotId).SingleAsync(ct);
        var fence = manager.AppSessionFence(robot); await fence.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            var o = await db.AppOperations.SingleAsync(x => x.Id == id, ct);
            if (!AppInstallationEndpoints.Pending.Contains(o.State) || o.Command is null) return;
            if (o.DeadlineAt <= DateTimeOffset.UtcNow && o.DispatchedAt is null)
            {
                o.State = "failed"; o.Phase = "failed"; o.CompletedAt = DateTimeOffset.UtcNow;
                o.Failure = new JsonObject { ["code"] = "deadline_exceeded", ["message"] = "Operation expired before dispatch.", ["details"] = new JsonObject { ["retryable"] = true } }.ToJsonString();
                var installation = await db.AppInstallations.SingleAsync(x => x.Id == o.InstallationId, ct);
                if (o.RunId is null) installation.Candidate = null;
                else if (o.Kind == "start")
                {
                    var run = await db.AppRuns.SingleAsync(x => x.Id == o.RunId, ct);
                    run.State = "failed"; run.Failure = o.Failure; run.CompletedAt = o.CompletedAt;
                    AppHistoryEndpoints.Event(db,run,"app.run.failed",o.CompletedAt!.Value,o.CompletedAt.Value);
                }
                // No device observation is invented for an undispatched failure.
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
            }
            if (o.Kind == "stop" && o.RunId is not null)
            {
                var run = await db.AppRuns.SingleAsync(x => x.Id == o.RunId, ct);
                if (!AppRunEndpoints.Active.Contains(run.State))
                {
                    o.State = "succeeded"; o.Phase = "stopped"; o.CompletedAt = DateTimeOffset.UtcNow;
                    o.ObservedAt = run.ObservedAt; o.ReceivedAt = run.ReceivedAt;
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
                }
            }
            var session = manager.Get(robot);
            if (session is null || session.Socket.State != System.Net.WebSockets.WebSocketState.Open ||
                !await db.RobotCredentials.AnyAsync(x => x.Id == session.CredentialId && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct)) return;
            var negotiated = await db.RobotAppInventories.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot && x.SessionId == session.SessionId && x.Supported, ct);
            if (negotiated is null) return;
            if (o.Kind == "uninstall" && (!negotiated.InstallationsSupported || !negotiated.UninstallSupported))
            {
                o.State = "failed"; o.Phase = "failed"; o.CompletedAt = DateTimeOffset.UtcNow;
                o.Failure = new JsonObject { ["code"] = "daemon_upgrade_required", ["message"] = "The current Daemon session does not support App uninstall.", ["details"] = new JsonObject { ["retryable"] = false } }.ToJsonString();
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
            }
            if (!negotiated.InstallationsSupported) return;
            if (o.RunId is not null && !await db.RobotAppInventories.AnyAsync(x => x.RobotId == robot && x.SessionId == session.SessionId && x.RunsSupported, ct)) return;
            if (o.Kind == "settings.apply" && !await db.RobotAppInventories.AnyAsync(x => x.RobotId == robot && x.SessionId == session.SessionId && x.ManifestV2Supported && x.LiveSettingsSupported, ct)) return;
            var command = JsonNode.Parse(o.Command)!;
            var snapshot = command["snapshot"];
            var release = command["release"];
            // Stop and other run-scoped commands may not carry a release or
            // startup snapshot. Only revision-bound commands need manifest
            // capability checks.
            var revisionNode = release ?? snapshot;
            var revisionId = revisionNode?["revision_id"]?.GetValue<string>();
            var manifestJson = revisionId is null ? null : await db.AppRevisions.AsNoTracking()
                .Where(x => x.Id == Guid.Parse(revisionId)).Select(x => x.Manifest).SingleOrDefaultAsync(ct);
            var schemaVersion = manifestJson is null ? 0 : JsonNode.Parse(manifestJson)!["schema_version"]!.GetValue<int>();
            if (schemaVersion == 2 && !await db.RobotAppInventories.AnyAsync(x => x.RobotId == robot && x.SessionId == session.SessionId && x.ManifestV2Supported, ct))
            {
                o.State = "failed"; o.Phase = "failed"; o.CompletedAt = DateTimeOffset.UtcNow;
                o.Failure = new JsonObject { ["code"] = "daemon_upgrade_required", ["message"] = "The current Daemon session does not support App manifest v2.", ["details"] = new JsonObject { ["retryable"] = false } }.ToJsonString();
                var installation = await db.AppInstallations.SingleAsync(x => x.Id == o.InstallationId, ct);
                if (o.RunId is null) installation.Candidate = null;
                else if (o.Kind == "start")
                {
                    var run = await db.AppRuns.SingleAsync(x => x.Id == o.RunId, ct);
                    run.State = "failed"; run.Failure = o.Failure; run.CompletedAt = o.CompletedAt;
                    AppHistoryEndpoints.Event(db, run, "app.run.failed", o.CompletedAt.Value, o.CompletedAt.Value);
                }
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
            }
            var ns = await db.RobotAppInventories.Where(x => x.RobotId == robot).Select(x => x.DeploymentNamespace).SingleAsync(ct);
            if (command["deployment_namespace"]!.GetValue<string>() != ns.ToString()) return;
            o.DispatchedAt ??= DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            await manager.SendAsync(session, RingfourJson.Options, "command", new JsonObject { ["operation_id"] = o.Id.ToString(), ["kind"] = "app." + o.Kind,
                ["payload_version"] = 1, ["payload"] = JsonNode.Parse(o.Command), ["deadline_at"] = AppProtocol.Timestamp(o.DeadlineAt) });
        }
        finally { fence.Release(); }
    }
}
