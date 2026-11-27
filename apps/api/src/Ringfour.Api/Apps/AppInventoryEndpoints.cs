using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Apps;

public static class AppInventoryEndpoints
{
    public static void MapAppInventoryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/device/app-protocol/negotiate", Negotiate).WithMetadata(new AppErrorResponse());
        routes.MapPut("/v1/device/app-inventory", ReportInventory).WithMetadata(new AppErrorResponse());
        routes.MapGet("/v1/robots/{robotId:guid}/app-status", Status).RequireAuthorization().WithMetadata(new AppErrorResponse());
        routes.MapGet("/v1/robots/{robotId:guid}/app-compatibility", Compatibility).RequireAuthorization().WithMetadata(new AppErrorResponse());
    }

    internal static async Task<Robot> Authenticate(HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"], out var id))
            throw ApiException.InvalidToken("A robot credential is required.");
        return (await DeviceAuth.VerifyCredential(db, id, context.Request.Headers["x-ringfour-credential-secret"], ct)).Robot;
    }

    internal static DeviceConnection Current(DeviceConnectionManager manager, Robot robot, Guid sessionId)
    {
        var current = manager.Get(robot.Id);
        if (current is null || current.SessionId != sessionId || current.Socket.State != WebSocketState.Open)
            throw ApiException.Conflict("session_replaced", "An active device session is required.");
        return current;
    }

    private static async Task<IResult> Negotiate(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager, CancellationToken ct)
    {
        var robot = await Authenticate(context, db, ct);
        var body = await AppProtocol.Read(context.Request, "negotiation_request", ct);
        var sessionId = Guid.Parse(body["session_id"]!.GetValue<string>());
        var ns = Guid.Parse(body["deployment_namespace"]!.GetValue<string>());
        var offers = body["supported_app_protocol_versions"]!.AsArray().Select(x => AppProtocol.Integer(x!)).Order().ToArray();
        var versions = System.Text.Json.JsonSerializer.Serialize(offers);
        var runSupport = body["supports_app_runs"]?.GetValue<bool>() ?? false;
        var installationSupport = body["supports_app_installations"]?.GetValue<bool>() ?? false;
        var uninstallSupport = body["supports_app_uninstall"]?.GetValue<bool>() ?? false;
        if (uninstallSupport && !installationSupport)
            throw ApiException.InvalidInput("App uninstall support requires installation support.");
        var manifestV2Support = body["supports_manifest_v2"]?.GetValue<bool>() ?? false;
        var liveSettingsSupport = body["supports_live_settings"]?.GetValue<bool>() ?? false;
        var fence = manager.AppSessionFence(robot.Id);
        await fence.WaitAsync(ct);
        try
        {
            Current(manager, robot, sessionId);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot.Id} FOR UPDATE").SingleAsync(ct);
            var row = await db.RobotAppInventories.SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
            if (row is not null && row.DeploymentNamespace != ns)
                throw ApiException.Conflict("ownership_conflict", "Enrollment is already bound to a deployment namespace.");
            if (row?.SessionId == sessionId && (!JsonNode.DeepEquals(JsonNode.Parse(row.OfferedVersions), JsonNode.Parse(versions)) || row.InstallationsSupported != installationSupport || row.UninstallSupported != uninstallSupport || row.RunsSupported != runSupport || row.ManifestV2Supported != manifestV2Support || row.LiveSettingsSupported != liveSettingsSupport))
                throw ApiException.Conflict("ownership_conflict", "Negotiation offers changed within this session.");
            if (row is null)
            {
                row = new RobotAppInventory { RobotId = robot.Id, DeploymentNamespace = ns };
                db.RobotAppInventories.Add(row);
            }
            if (row.SessionId != sessionId)
            {
                row.SessionId = sessionId; row.NegotiatedAt = DateTimeOffset.UtcNow;
                row.OfferedVersions = versions; row.Supported = offers.Contains(1L); row.InstallationsSupported = installationSupport; row.UninstallSupported = uninstallSupport; row.RunsSupported = runSupport;
                row.ManifestV2Supported = manifestV2Support; row.LiveSettingsSupported = liveSettingsSupport;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (!row.Supported) throw ApiException.Conflict("daemon_upgrade_required", "No supported App protocol was offered.");
            return Results.Json(new JsonObject { ["session_id"] = sessionId.ToString(), ["deployment_namespace"] = ns.ToString(),
                ["selected_app_protocol_version"] = 1, ["server_time"] = AppProtocol.Timestamp(row.NegotiatedAt) });
        }
        finally { fence.Release(); }
    }

    private static async Task<IResult> ReportInventory(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager, CancellationToken ct)
    {
        var robot = await Authenticate(context, db, ct);
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-device-session"], out var sessionId))
            throw ApiException.Conflict("session_replaced", "An active negotiated session is required.");
        var body = await AppProtocol.Read(context.Request, "inventory_report", ct);
        var ns = Guid.Parse(body["deployment_namespace"]!.GetValue<string>());
        var reportId = Guid.Parse(body["report_id"]!.GetValue<string>());
        var sequence = AppProtocol.Integer(body["report_sequence"]!);
        var digest = AppProtocol.Digest(body);
        var fence = manager.AppSessionFence(robot.Id);
        await fence.WaitAsync(ct);
        try
        {
            Current(manager, robot, sessionId);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot.Id} FOR UPDATE").SingleAsync(ct);
            var row = await db.RobotAppInventories.SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
            if (row is null || row.SessionId != sessionId || !row.Supported)
                throw ApiException.Conflict("daemon_upgrade_required", "Negotiate App support on this session first.");
            if (row.DeploymentNamespace != ns)
                throw ApiException.Conflict("ownership_conflict", "Deployment namespace mismatch.");
            var previous = await db.AppDeviceReports.SingleOrDefaultAsync(x => x.RobotId == robot.Id && x.ReportId == reportId, ct);
            if (previous is not null)
            {
                if (previous.Route != "inventory" || previous.BodySha256 != digest)
                    throw ApiException.Conflict("conflicting_report", "Report ID was reused with different content.");
                return Results.Json(JsonNode.Parse(previous.Receipt));
            }
            if (sequence != row.ReportSequence + 1)
                throw ApiException.Conflict("stale_observation", "Inventory sequence must advance by exactly one.");
            var now = DateTimeOffset.UtcNow;
            var receipt = new JsonObject { ["report_id"] = reportId.ToString(), ["report_sequence"] = sequence,
                ["received_at"] = AppProtocol.Timestamp(now) };
            row.ReportSequence = sequence; row.ReceivedAt = now;
            row.Platform = body["platform"]!.ToJsonString(); row.Inventory = body["inventory"]!.ToJsonString();
            db.AppDeviceReports.Add(new AppDeviceReport { RobotId = robot.Id, ReportId = reportId, DeploymentNamespace = ns,
                Sequence = sequence, BodySha256 = digest, Receipt = receipt.ToJsonString() });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Json(receipt);
        }
        finally { fence.Release(); }
    }

    private static async Task<JsonObject> Projection(Robot robot, RingfourDbContext db, DeviceConnectionManager manager, IConfiguration config, CancellationToken ct)
    {
        var row = await db.RobotAppInventories.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
        var presence = await db.RobotPresences.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
        var session = manager.Get(robot.Id);
        var online = session?.Socket.State == WebSocketState.Open && presence?.SessionId == session.SessionId &&
            presence.LastSeenAt >= DateTimeOffset.UtcNow.AddSeconds(-config.GetValue("Connectivity:OfflineThresholdSeconds", 45));
        var negotiated = online && row is not null && row.SessionId == session!.SessionId;
        return new JsonObject {
            ["robot_id"] = robot.Id.ToString(), ["connectivity"] = online ? "online" : "offline",
            ["last_seen_at"] = presence is null ? null : AppProtocol.Timestamp(presence.LastSeenAt),
            ["daemon_version"] = presence?.DaemonVersion,
            ["recommended_daemon_version"] = config["Apps:RecommendedDaemonVersion"] ?? "1.0.0",
            ["support"] = negotiated ? row!.Supported ? "supported" : "upgrade_required" : "not_negotiated",
            ["supports_manifest_v2"] = negotiated ? row!.ManifestV2Supported : null,
            ["supports_live_settings"] = negotiated ? row!.LiveSettingsSupported : null,
            ["supports_app_uninstall"] = negotiated ? row!.UninstallSupported : null,
            ["negotiated_at"] = row is null ? null : AppProtocol.Timestamp(row.NegotiatedAt),
            ["deployment_namespace"] = row?.DeploymentNamespace.ToString(), ["report_sequence"] = row?.ReportSequence ?? 0,
            ["received_at"] = row?.ReceivedAt is null ? null : AppProtocol.Timestamp(row.ReceivedAt.Value),
            ["platform"] = row?.Platform is null ? null : JsonNode.Parse(row.Platform),
            ["inventory"] = row?.Inventory is null ? null : JsonNode.Parse(row.Inventory),
        };
    }

    private static async Task<IResult> Status(Guid robotId, HttpContext context, RingfourDbContext db, DeviceConnectionManager manager, IConfiguration config, CancellationToken ct)
    {
        var (_, robot) = await RobotAccess.RequireRobotAccess(context, db, robotId, OrgRole.Operator, ct);
        return Results.Json(await Projection(robot, db, manager, config, ct));
    }

    private static async Task<IResult> Compatibility(Guid robotId, [FromQuery(Name = "revision_id")] Guid revisionId, HttpContext context, RingfourDbContext db, DeviceConnectionManager manager, IConfiguration config, CancellationToken ct)
    {
        // Bind explicitly because the wire query name is revision_id.
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, robotId, OrgRole.Operator, ct);
        var revision = await db.AppRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, ct);
        var now = DateTimeOffset.UtcNow;
        if (revision is null || !(revision.Visibility == "listed" ||
            await db.Memberships.AnyAsync(x => x.UserId == user.Id && x.OrganizationId == revision.PublisherOrganizationId, ct) ||
            await db.AppImports.AnyAsync(x => x.OrganizationId == robot.OrganizationId && x.RevisionId == revisionId, ct) ||
            await db.AppShares.AnyAsync(x => x.DestinationOrganizationId == robot.OrganizationId && x.RevisionId == revisionId && x.RevokedAt == null && x.ExpiresAt > now, ct)))
            throw ApiException.NotFound("The requested revision is not accessible.");
        var projection = await Projection(robot, db, manager, config, ct);
        return Results.Json(AppCompatibility.Evaluate(JsonNode.Parse(revision.Manifest)!.AsObject(), projection, now,
            config.GetValue("Connectivity:OfflineThresholdSeconds", 45)));
    }
}
