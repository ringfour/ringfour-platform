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
            if (operation?.Command is null || JsonNode.Parse(operation.Command)!["release"]!["revision_id"]!.GetValue<string>() != revisionId.ToString())
                throw ApiException.NotFound("No authorized preparation operation.");
            if (operation.DeadlineAt <= DateTimeOffset.UtcNow) throw ApiException.Conflict("deadline_exceeded", "Operation expired.");
            var revision = await db.AppRevisions.AsNoTracking().SingleAsync(x => x.Id == revisionId, ct);
            return Results.Json(AppCatalogEndpoints.RevisionJson(revision));
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
            if (!await db.RobotAppInventories.AnyAsync(x => x.RobotId == session.RobotId && x.SessionId == session.SessionId && x.InstallationsSupported, ct)) return true;
            await db.AppOperations.Where(x => x.Id == id && x.RobotId == session.RobotId && x.State == "queued")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, "accepted").SetProperty(x => x.Phase, "accepted").SetProperty(x => x.AcknowledgedAt, DateTimeOffset.UtcNow), ct);
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
            if (body["runs"]!.AsArray().Count != 0 || ops.Count != installations.Count ||
                ops.Select(x => x!["operation_id"]!.ToString()).Distinct().Count() != ops.Count || installations.Select(x => x!["installation_id"]!.ToString()).Distinct().Count() != installations.Count) throw Invalid();
            var now = DateTimeOffset.UtcNow;
            foreach (var item in ops)
            {
                var operationId = Guid.Parse(item!["operation_id"]!.GetValue<string>());
                var o = await db.AppOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.RobotId == robot.Id, ct) ?? throw Invalid();
                if (o.Command is null || o.Kind is not ("install" or "configure" or "update" or "rollback") || o.RunId is not null || item["run_id"] is not null || item["kind"]!.GetValue<string>() != o.Kind || item["installation_id"]!.GetValue<string>() != o.InstallationId.ToString()) throw Invalid();
                var observed = DateTimeOffset.Parse(item["observed_at"]!.GetValue<string>());
                var state = item["state"]!.GetValue<string>(); var phase = item["phase"]!.GetValue<string>();
                var incoming = installations.SingleOrDefault(x => x!["installation_id"]!.GetValue<string>() == o.InstallationId.ToString()) ?? throw Invalid();
                if (state == "succeeded") Match(incoming["installed"] ?? throw Invalid(), JsonNode.Parse(o.Command)!["release"]!, operationId, true);
                if (!AppInstallationEndpoints.Pending.Contains(o.State))
                {
                    if (o.State != state || o.Phase != phase || o.ObservedAt != observed || !Same(o.Failure is null ? null : JsonNode.Parse(o.Failure), item["failure"])) throw Invalid();
                    continue; // No stale report can overwrite an intervening binding.
                }
                var phases = new[] { "queued", "accepted", "checking", "staging", "staged", "applied" };
                if (state == "cancelled" || (state != "failed" && Array.IndexOf(phases,phase) < Array.IndexOf(phases,o.Phase))) throw Invalid();
                var installation = await db.AppInstallations.SingleAsync(x => x.Id == o.InstallationId, ct);
                var candidate = installation.Candidate is null ? null : JsonNode.Parse(installation.Candidate)!.AsObject();
                if (candidate?["operation_id"]!.GetValue<string>() != operationId.ToString()) throw Invalid();
                var command = JsonNode.Parse(o.Command)!; var release = command["release"]!;
                var oldBinding = installation.InstalledBinding is null ? null : JsonNode.Parse(installation.InstalledBinding)!.AsObject();
                if (!Same(command["expected_revision_id"],oldBinding?["revision_id"]) || !Same(command["expected_configuration_revision_id"],oldBinding?["configuration_revision_id"])) throw Invalid();
                if (state == "succeeded")
                {
                    if (phase != "applied" || incoming["installed"] is null || incoming["candidate"] is not null) throw Invalid();
                    Match(incoming["installed"]!,release,operationId,true);
                    await AppInstallationEndpoints.RequireIdle(db,robot.Id,ct);
                    var binding = incoming["installed"]!.DeepClone().AsObject(); binding["observed_at"] = incoming["observed_at"]!.DeepClone();
                    installation.InstalledBinding = binding.ToJsonString(); installation.Candidate = null;
                }
                else
                {
                    oldBinding?.Remove("observed_at");
                    if (!Same(oldBinding,incoming["installed"]) || incoming["candidate"] is null) throw Invalid();
                    var staged = incoming["candidate"]!; Match(staged,release,operationId,false);
                    if ((state == "failed") != (staged["state"]!.GetValue<string>() == "failed") || !Same(staged["failure"],item["failure"])) throw Invalid();
                    if ((phase == "staged") != (staged["state"]!.GetValue<string>() == "staged")) throw Invalid();
                    candidate["state"] = staged["state"]!.DeepClone(); candidate["observed_at"] = incoming["observed_at"]!.DeepClone();
                    candidate["staged_at"] = staged["staged_at"]?.DeepClone(); candidate["failure"] = staged["failure"]?.DeepClone();
                    installation.Candidate = candidate.ToJsonString();
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

public sealed class AppInstallationWorker(IServiceScopeFactory scopes, DeviceConnectionManager manager, IConfiguration config, ILogger<AppInstallationWorker> logger) : BackgroundService
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
            try { await Task.Delay(config.GetValue("Apps:InstallationSweepMilliseconds", 2000), stoppingToken); }
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
                installation.Candidate = null;
                // No device observation is invented for an undispatched failure.
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
            }
            var session = manager.Get(robot);
            if (session is null || session.Socket.State != System.Net.WebSockets.WebSocketState.Open ||
                !await db.RobotCredentials.AnyAsync(x => x.Id == session.CredentialId && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct) ||
                !await db.RobotAppInventories.AnyAsync(x => x.RobotId == robot && x.SessionId == session.SessionId && x.Supported && x.InstallationsSupported,ct)) return;
            var ns = await db.RobotAppInventories.Where(x => x.RobotId == robot).Select(x => x.DeploymentNamespace).SingleAsync(ct);
            if (JsonNode.Parse(o.Command)!["deployment_namespace"]!.GetValue<string>() != ns.ToString()) return;
            o.DispatchedAt ??= DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            await manager.SendAsync(session, RingfourJson.Options, "command", new JsonObject { ["operation_id"] = o.Id.ToString(), ["kind"] = "app." + o.Kind,
                ["payload_version"] = 1, ["payload"] = JsonNode.Parse(o.Command), ["deadline_at"] = AppProtocol.Timestamp(o.DeadlineAt) });
        }
        finally { fence.Release(); }
    }
}
