using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

public static class AppRegistryEndpoints
{
    public static void MapAppRegistryEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/device/app-registry-grants", Grant).WithMetadata(new AppErrorResponse());

    private static async Task<(JsonObject Target, DateTimeOffset Deadline, Guid Publisher)> Binding(RingfourDbContext db, Guid robotId, JsonObject request, CancellationToken ct)
    {
        var operationId = Guid.Parse(request["operation_id"]!.GetValue<string>());
        var revisionId = Guid.Parse(request["revision_id"]!.GetValue<string>());
        var image = request["image"]!.GetValue<string>();
        var operation = await db.AppOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId && x.RobotId == robotId, ct);
        if (operation is null || operation.Kind is not ("install" or "update" or "rollback") || operation.State is not ("queued" or "accepted" or "running"))
            throw ApiException.NotFound("No authorized preparation operation.");
        if (operation.DeadlineAt <= DateTimeOffset.UtcNow) throw ApiException.Conflict("deadline_exceeded", "Operation deadline has passed.");
        var installation = await db.AppInstallations.AsNoTracking().SingleAsync(x => x.Id == operation.InstallationId, ct);
        var candidate = installation.Candidate is null ? null : JsonNode.Parse(installation.Candidate);
        var revision = await db.AppRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId && x.AppId == installation.AppId, ct);
        if (candidate?["operation_id"]?.GetValue<string>() != operationId.ToString() || candidate["revision_id"]?.GetValue<string>() != revisionId.ToString() ||
            candidate["image"]?.GetValue<string>() != image || candidate["state"]?.GetValue<string>() is not ("staging" or "staged") || revision is null ||
            !await db.AppImports.AnyAsync(x => x.OrganizationId == operation.OrganizationId && x.RevisionId == revisionId, ct))
            throw ApiException.NotFound("No authorized candidate image.");
        var target = JsonNode.Parse(revision.Manifest)!["targets"]!.AsArray().SingleOrDefault(x => x!["image"]!.GetValue<string>() == image);
        return (target?.DeepClone().AsObject() ?? throw ApiException.NotFound("Image is not a revision target."), operation.DeadlineAt, revision.PublisherOrganizationId);
    }

    private static async Task<IResult> Grant(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager, AppRegistry registry, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var robot = await AppInventoryEndpoints.Authenticate(context, db, ct);
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-device-session"], out var sessionId))
            throw ApiException.Conflict("session_replaced", "An active negotiated session is required.");
        var request = await AppProtocol.Read(context.Request, "request_registry_grant", ct);
        async Task Session()
        {
            AppInventoryEndpoints.Current(manager, robot, sessionId);
            if (!await db.RobotAppInventories.AsNoTracking().AnyAsync(x => x.RobotId == robot.Id && x.SessionId == sessionId && x.Supported, ct))
                throw ApiException.Conflict("daemon_upgrade_required", "Negotiate App support on this session first.");
        }
        await Session();
        var (target, _, publisher) = await Binding(db, robot.Id, request, ct);
        var authorization = await registry.Verify(target, publisher, ct);
        var fence = manager.AppSessionFence(robot.Id);
        await fence.WaitAsync(ct);
        try
        {
            db.ChangeTracker.Clear(); // Reload credential revocation after the remote exchange.
            await AppInventoryEndpoints.Authenticate(context, db, ct);
            await Session();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var operationId = Guid.Parse(request["operation_id"]!.GetValue<string>());
            var installationId = await db.AppOperations.Where(x => x.Id == operationId && x.RobotId == robot.Id).Select(x => x.InstallationId).SingleAsync(ct);
            await db.AppInstallations.FromSqlInterpolated($"SELECT * FROM app_installations WHERE id = {installationId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            await db.AppOperations.FromSqlInterpolated($"SELECT * FROM app_operations WHERE id = {operationId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            var (_, deadline, _) = await Binding(db, robot.Id, request, ct);
            if (authorization.ExpiresAt <= DateTimeOffset.UtcNow) throw new ApiException(503, "revision_unavailable", "Registry credential expired during verification.") { Retryable = true };
            var result = new JsonObject { ["robot_id"] = robot.Id.ToString(), ["operation_id"] = operationId.ToString(),
                ["revision_id"] = request["revision_id"]!.DeepClone(), ["image"] = request["image"]!.DeepClone(),
                ["registry"] = authorization.Registry, ["repository"] = authorization.Repository,
                ["expires_at"] = AppProtocol.Timestamp(authorization.ExpiresAt < deadline ? authorization.ExpiresAt : deadline),
                ["authorization"] = authorization.Token is null ? new JsonObject { ["kind"] = "anonymous" } :
                    new JsonObject { ["kind"] = "bearer", ["token"] = authorization.Token } };
            await transaction.CommitAsync(ct);
            return Results.Json(result);
        }
        finally { fence.Release(); }
    }
}
