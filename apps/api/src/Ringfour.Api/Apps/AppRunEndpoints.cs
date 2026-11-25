using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Apps;

public static class AppRunEndpoints
{
    internal static readonly string[] Active = ["accepted", "running"];
    public static void MapAppRunEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/v1").RequireAuthorization().WithMetadata(new AppErrorResponse());
        g.MapPost("/app-installations/{installationId:guid}/start", Start);
        g.MapPost("/app-installations/{installationId:guid}/stop", Stop);
        g.MapGet("/app-runs/{runId:guid}", Get);
        g.MapGet("/organizations/{orgId:guid}/app-runs", List);
    }
    private static Task<IResult> Start(Guid installationId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct) => Change("start", installationId, c, db, config, ct);
    private static Task<IResult> Stop(Guid installationId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct) => Change("stop", installationId, c, db, config, ct);
    private static async Task<IResult> Change(string kind, Guid id, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct)
    {
        var installation = await db.AppInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Installation is not accessible.");
        var (user, robot) = await RobotAccess.RequireRobotAccess(c, db, installation.RobotId, OrgRole.Operator, ct);
        var body = await AppProtocol.Read(c.Request, "request_" + kind, ct);
        return await AppCatalogEndpoints.Mutate(c, db, user, robot.OrganizationId, "app." + kind, id.ToString(), body, 202, async () =>
        {
            if (!config.GetValue("Apps:InstallationsEnabled", false)) throw new ApiException(503, "daemon_upgrade_required", "Enable the App consumers first.");
            await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            var current = await db.AppInstallations.SingleAsync(x => x.Id == id, ct);
            var inventory = await db.RobotAppInventories.SingleOrDefaultAsync(x => x.RobotId == robot.Id && x.Supported && x.InstallationsSupported && x.RunsSupported, ct)
                ?? throw ApiException.Conflict("daemon_upgrade_required", "An App-capable Daemon is required.");
            var now = DateTimeOffset.UtcNow;
            var operation = new AppOperation { Id = Guid.NewGuid(), OrganizationId = robot.OrganizationId, RobotId = robot.Id, InstallationId = id, Kind = kind,
                State = "queued", Phase = "queued", RequestedByUserId = user.Id, RequestedAt = now, DeadlineAt = now.AddMinutes(10) };
            AppRun run;
            if (kind == "start")
            {
                await AppInstallationEndpoints.RequireIdle(db, robot.Id, ct);
                await AppInstallationEndpoints.RequireNoPendingMutation(db, robot.Id, ct);
                var binding = current.InstalledBinding is null ? null : JsonNode.Parse(current.InstalledBinding);
                if (binding is null || !JsonNode.DeepEquals(body["expected_revision_id"], binding["revision_id"]) || !JsonNode.DeepEquals(body["expected_configuration_revision_id"], binding["configuration_revision_id"]))
                    throw ApiException.Conflict("stale_installation", "Installed revision/configuration changed.");
                var revision = await db.AppRevisions.SingleAsync(x => x.Id == Guid.Parse(binding["revision_id"]!.GetValue<string>()), ct);
                var configuration = await db.AppConfigurationRevisions.SingleAsync(x => x.Id == Guid.Parse(binding["configuration_revision_id"]!.GetValue<string>()) && x.InstallationId == id, ct);
                var manifest = AppManifest.Validate(revision.Manifest);
                if (manifest["schema_version"]!.GetValue<int>() == 2 && !inventory.ManifestV2Supported)
                    throw ApiException.Conflict("daemon_upgrade_required", "The current Daemon session does not support App manifest v2.");
                JsonNode? runtimeSettingsValues = null;
                Guid? runtimeSettingsRevisionId = null;
                Guid? initialSettingsApplicationId = null;
                AppSettingsRevision? initialSettingsRevision = null;
                if (manifest["runtime_settings"] is JsonObject runtimeSettings)
                {
                    if (!config.GetValue("Apps:LiveSettingsEnabled", false) || !inventory.LiveSettingsSupported)
                        throw ApiException.Conflict("daemon_upgrade_required", "The current Daemon session does not support live settings.");
                    if (current.SelectedSettingsRevisionId is Guid selectedId)
                    {
                        initialSettingsRevision = await db.AppSettingsRevisions.SingleAsync(x => x.Id == selectedId && x.InstallationId == id, ct);
                        if (initialSettingsRevision.RevisionId != revision.Id)
                            throw ApiException.Conflict("stale_settings_selection", "Save settings against the installed App release before starting it.");
                        runtimeSettingsValues = AppManifest.ValidateRuntimeSettings(manifest, JsonNode.Parse(initialSettingsRevision.Values)!);
                    }
                    else
                    {
                        runtimeSettingsValues = AppManifest.ValidateRuntimeSettings(manifest, runtimeSettings["defaults"]!);
                        initialSettingsRevision = new AppSettingsRevision
                        {
                            Id = Guid.NewGuid(), InstallationId = id, AppId = current.AppId, RevisionId = revision.Id,
                            Values = runtimeSettingsValues.ToJsonString(), CreatedByUserId = user.Id, CreatedAt = now
                        };
                        db.AppSettingsRevisions.Add(initialSettingsRevision);
                    }
                    runtimeSettingsRevisionId = initialSettingsRevision.Id;
                    initialSettingsApplicationId = Guid.NewGuid();
                }
                var configurationValues = JsonNode.Parse(configuration.Values)!;
                var snapshot = new JsonObject { ["app_id"] = current.AppId.ToString(), ["revision_id"] = revision.Id.ToString(), ["import_id"] = current.ImportId.ToString(),
                    ["publisher_organization_id"] = revision.PublisherOrganizationId.ToString(), ["manifest_digest"] = revision.ManifestDigest,
                    ["configuration_revision_id"] = configuration.Id.ToString(), ["configuration"] = configurationValues,
                    ["configuration_environment"] = AppManifest.Environment(manifest, configurationValues),
                    ["runtime_settings_revision_id"] = runtimeSettingsRevisionId?.ToString(),
                    ["runtime_settings"] = runtimeSettingsValues?.DeepClone(),
                    ["initial_settings_application_id"] = initialSettingsApplicationId?.ToString(),
                    ["image"] = binding["image"]!.DeepClone() };
                run = new AppRun { Id = Guid.NewGuid(), OrganizationId = robot.OrganizationId, RobotId = robot.Id, InstallationId = id, StartOperationId = operation.Id,
                    Snapshot = snapshot.ToJsonString(), RequestedByUserId = user.Id, RequestedAt = now };
                operation.Command = new JsonObject { ["app_protocol_version"] = 1, ["deployment_namespace"] = inventory.DeploymentNamespace.ToString(), ["installation_id"] = id.ToString(), ["run_id"] = run.Id.ToString(), ["snapshot"] = snapshot }.ToJsonString();
                // Break the operation/run FK cycle inside the enclosing transaction.
                db.AppOperations.Add(operation); await db.SaveChangesAsync(ct);
                db.AppRuns.Add(run); await db.SaveChangesAsync(ct);
                if (initialSettingsRevision is not null && initialSettingsApplicationId is Guid initialId)
                {
                    var settingsCommand = new JsonObject
                    {
                        ["app_protocol_version"] = 1, ["deployment_namespace"] = inventory.DeploymentNamespace.ToString(),
                        ["installation_id"] = id.ToString(), ["run_id"] = run.Id.ToString(), ["application_id"] = initialId.ToString(),
                        ["settings_revision_id"] = initialSettingsRevision.Id.ToString(), ["expected_applied_revision_id"] = null,
                        ["values"] = runtimeSettingsValues!.DeepClone(), ["initial"] = true
                    };
                    AppProtocol.Validate(settingsCommand.ToJsonString(), "settings_apply_command");
                    db.AppOperations.Add(new AppOperation
                    {
                        Id = initialId, OrganizationId = robot.OrganizationId, RobotId = robot.Id, InstallationId = id,
                        RunId = run.Id, Kind = "settings.apply", State = "queued", Phase = "queued", Command = settingsCommand.ToJsonString(),
                        RequestedByUserId = user.Id, RequestedAt = now, DeadlineAt = now.AddMinutes(10)
                    });
                    db.AppSettingsApplications.Add(new AppSettingsApplication
                    {
                        ApplicationId = initialId, InstallationId = id, RunId = run.Id,
                        SettingsRevisionId = initialSettingsRevision.Id, ExpectedAppliedRevisionId = null,
                        ExpectedSelectedRevisionId = current.SelectedSettingsRevisionId, Initial = true,
                        State = "pending", DeliveryState = "queued", RequestedByUserId = user.Id, RequestedAt = now
                    });
                    AppHistoryEndpoints.SettingsEvent(db, robot.Id, id, initialId, initialId, run.Id, user.Id,
                        "app.settings.application_requested", new JsonObject
                        {
                            ["application_id"] = initialId.ToString(), ["settings_revision_id"] = initialSettingsRevision.Id.ToString(),
                            ["state"] = "pending", ["initial"] = true
                        }, now);
                    await db.SaveChangesAsync(ct);
                }
            }
            else
            {
                var runId = Guid.Parse(body["expected_run_id"]!.GetValue<string>());
                run = await db.AppRuns.SingleOrDefaultAsync(x => x.Id == runId && x.InstallationId == id, ct) ?? throw ApiException.NotFound("Run is not accessible.");
                operation.Command = new JsonObject { ["app_protocol_version"] = 1, ["deployment_namespace"] = inventory.DeploymentNamespace.ToString(), ["installation_id"] = id.ToString(), ["expected_run_id"] = run.Id.ToString() }.ToJsonString();
                if (!Active.Contains(run.State)) { operation.State = "succeeded"; operation.Phase = "stopped"; operation.CompletedAt = now; }
                db.AppOperations.Add(operation);
            }
            operation.RunId = run.Id;
            return new JsonObject { ["installation_id"] = id.ToString(), ["operation_id"] = operation.Id.ToString(), ["run_id"] = run.Id.ToString() };
        }, ct, OrgRole.Operator);
    }
    internal static JsonObject RunJson(AppRun r) => new() {
        ["id"] = r.Id.ToString(), ["organization_id"] = r.OrganizationId.ToString(), ["robot_id"] = r.RobotId.ToString(), ["installation_id"] = r.InstallationId.ToString(),
        ["start_operation_id"] = r.StartOperationId.ToString(), ["snapshot"] = JsonNode.Parse(r.Snapshot), ["state"] = r.State, ["requested_by"] = r.RequestedByUserId.ToString(),
        ["requested_at"] = AppProtocol.Timestamp(r.RequestedAt), ["started_at"] = AppInstallationEndpoints.Stamp(r.StartedAt), ["ended_at"] = AppInstallationEndpoints.Stamp(r.EndedAt),
        ["observed_at"] = AppInstallationEndpoints.Stamp(r.ObservedAt), ["received_at"] = AppInstallationEndpoints.Stamp(r.ReceivedAt), ["exit_code"] = r.ExitCode,
        ["failure"] = r.Failure is null ? null : JsonNode.Parse(r.Failure), ["completed_at"] = AppInstallationEndpoints.Stamp(r.CompletedAt) };
    internal static async Task<AppRun> Read(Guid id, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        var run = await db.AppRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Run is not accessible.");
        await RobotAccess.RequireRobotAccess(c, db, run.RobotId, OrgRole.Operator, ct); return run;
    }
    private static async Task<IResult> Get(Guid runId, HttpContext c, RingfourDbContext db, CancellationToken ct) => Results.Json(RunJson(await Read(runId, c, db, ct)));
    private static async Task<IResult> List(Guid orgId, Guid? robot_id, Guid? installation_id, Guid? app_id,
        bool? active_only, bool? recent_first, string? cursor, int? limit, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(c), ct); await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Operator, ct);
        if (limit is < 1 or > 100 || cursor?.Length > 1024) throw ApiException.InvalidInput("Invalid page limit/cursor.");
        var (at, id) = Pagination.DecodeCursor(cursor); var size = limit ?? 50;
        var query = db.AppRuns.AsNoTracking().Where(x => x.OrganizationId == orgId);
        if (robot_id is not null) query = query.Where(x => x.RobotId == robot_id);
        if (installation_id is not null) query = query.Where(x => x.InstallationId == installation_id);
        if (app_id is not null) query = query.Where(x => db.AppInstallations.Any(i => i.Id == x.InstallationId && i.AppId == app_id));
        if (active_only == true) query = query.Where(x => x.State == "accepted" || x.State == "running");
        if (at is not null)
            query = recent_first == true
                ? query.Where(x => x.RequestedAt < at || (x.RequestedAt == at && x.Id.CompareTo(id!.Value) < 0))
                : query.Where(x => x.RequestedAt > at || (x.RequestedAt == at && x.Id.CompareTo(id!.Value) > 0));
        var rows = recent_first == true
            ? await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct)
            : await query.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id).Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();
        return Results.Json(new { items = page.Select(RunJson), next_cursor = Pagination.NextCursor(rows.Count > size, page, x => x.RequestedAt, x => x.Id) });
    }
}
