using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Apps;

public static class AppInstallationEndpoints
{
    internal static readonly string[] Pending = ["queued", "accepted", "running"];
    public static void MapAppInstallationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1").RequireAuthorization().WithMetadata(new AppErrorResponse());
        group.MapPost("/robots/{robotId:guid}/app-installations", Install);
        group.MapGet("/robots/{robotId:guid}/app-installations", List);
        group.MapGet("/app-installations/{installationId:guid}", Get);
        group.MapPost("/app-installations/{installationId:guid}/configure", Configure);
        group.MapPost("/app-installations/{installationId:guid}/update", Update);
        group.MapPost("/app-installations/{installationId:guid}/rollback", Rollback);
        group.MapGet("/app-installations/{installationId:guid}/configuration-revisions/{configurationRevisionId:guid}", Configuration);
        group.MapGet("/app-operations/{operationId:guid}", Operation);
    }

    private static Task<IResult> Install(Guid robotId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct) => Change("install", robotId, null, c, db, config, ct);
    private static Task<IResult> Configure(Guid installationId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct) => Change("configure", null, installationId, c, db, config, ct);
    private static Task<IResult> Update(Guid installationId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct) => Change("update", null, installationId, c, db, config, ct);
    private static Task<IResult> Rollback(Guid installationId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct) => Change("rollback", null, installationId, c, db, config, ct);

    internal static async Task RequireIdle(RingfourDbContext db, Guid robot, CancellationToken ct)
    {
        if (await db.RobotExecutionSlots.AnyAsync(x => x.RobotId == robot, ct))
            throw ApiException.Conflict("execution_conflict", "The robot must be idle to change an installed binding.");
    }

    internal static async Task RequireNoPendingMutation(RingfourDbContext db, Guid robot, CancellationToken ct)
    {
        if (await db.RobotExecutionSlots.AnyAsync(x => x.RobotId == robot, ct) ||
            await db.AppOperations.AnyAsync(x => x.RobotId == robot && Pending.Contains(x.State) && x.RunId == null, ct))
            throw ApiException.Conflict("execution_conflict", "An App installation change is pending.");
    }

    public static JsonObject ValidateConfiguration(JsonObject manifest, JsonNode value)
    {
        if (AppProtocol.Canonical(value).Length > 16384) throw ApiException.InvalidInput("Configuration exceeds 16384 bytes.");
        try { AppManifest.Evaluate(manifest["configuration"]!["schema"]!.AsObject(), value); }
        catch (ApiException) { throw new ApiException(400, "invalid_config", "Configuration violates this release schema."); }
        AppManifest.ValidateEnvironmentValues(manifest, value);
        return value.DeepClone().AsObject();
    }

    private static async Task<IResult> Change(string kind, Guid? robotId, Guid? installationId, HttpContext c, RingfourDbContext db, IConfiguration config, CancellationToken ct)
    {
        var existing = installationId is null ? null : await db.AppInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId, ct)
            ?? throw ApiException.NotFound("Installation is not accessible.");
        var (user, robot) = await RobotAccess.RequireRobotAccess(c, db, robotId ?? existing!.RobotId, OrgRole.Developer, ct);
        var body = await AppProtocol.Read(c.Request, "request_" + kind, ct);
        return await AppCatalogEndpoints.Mutate(c, db, user, robot.OrganizationId, "app." + kind, (robotId ?? installationId)!.Value.ToString(), body, 202, async () =>
        {
            if (!config.GetValue("Apps:InstallationsEnabled", false)) throw new ApiException(503, "daemon_upgrade_required", "Deploy and enable both installation consumers first.");
            await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            await RequireIdle(db, robot.Id, ct);
            var now = DateTimeOffset.UtcNow;
            var inventory = await db.RobotAppInventories.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
            if (inventory is null || !inventory.InstallationsSupported || inventory.Platform is null)
                throw ApiException.Conflict("daemon_upgrade_required", "An installation-capable Daemon with an observed engine target is required.");
            AppInstallation installation;
            if (kind == "install")
            {
                var imported = await AppCatalogEndpoints.EnsureImport(db, user, robot.OrganizationId, body, ct);
                var prior = await db.AppInstallations.SingleOrDefaultAsync(x => x.RobotId == robot.Id && x.AppId == imported.AppId, ct);
                if (prior?.InstalledBinding is not null) throw ApiException.Conflict("installation_exists", "Use the existing installation's update operation.");
                if (prior is not null && await db.AppOperations.AnyAsync(x => x.InstallationId == prior.Id && Pending.Contains(x.State), ct))
                    throw ApiException.Conflict("stale_installation", "The initial installation is pending.");
                installation = prior ?? new AppInstallation { Id = Guid.NewGuid(), RobotId = robot.Id, OrganizationId = robot.OrganizationId,
                    AppId = imported.AppId, ImportId = imported.Id, CreatedByUserId = user.Id, CreatedAt = now };
                if (prior is null) db.AppInstallations.Add(installation);
            }
            else
            {
                installation = await db.AppInstallations.SingleAsync(x => x.Id == installationId, ct);
                if (await db.AppOperations.AnyAsync(x => x.InstallationId == installation.Id && Pending.Contains(x.State), ct))
                    throw ApiException.Conflict("stale_installation", "An installation change is already pending.");
            }
            var installed = installation.InstalledBinding is null ? null : JsonNode.Parse(installation.InstalledBinding)!.AsObject();
            if (kind != "install" && (installed is null || !JsonNode.DeepEquals(body["expected_configuration_revision_id"], installed["configuration_revision_id"]) ||
                (kind != "configure" && !JsonNode.DeepEquals(body["expected_revision_id"], installed["revision_id"]))))
                throw ApiException.Conflict("stale_installation", "Installed revision/configuration changed.");
            var revisionId = kind == "configure" ? Guid.Parse(installed!["revision_id"]!.GetValue<string>()) : Guid.Parse(body["revision_id"]!.GetValue<string>());
            var revision = await db.AppRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId && x.AppId == installation.AppId, ct)
                ?? throw ApiException.NotFound("Revision is not accessible.");
            if (kind == "update") await AppCatalogEndpoints.EnsureImport(db, user, robot.OrganizationId, body, ct);
            var manifest = AppManifest.Validate(revision.Manifest);
            if (manifest["schema_version"]!.GetValue<int>() == 2 && !inventory.ManifestV2Supported)
                throw ApiException.Conflict("daemon_upgrade_required", "The current Daemon session does not support App manifest v2.");
            var platform = JsonNode.Parse(inventory.Platform)!;
            var target = manifest["targets"]!.AsArray().SingleOrDefault(x => JsonNode.DeepEquals(x!["os"], platform["os"]) && JsonNode.DeepEquals(x["architecture"], platform["architecture"]));
            if (target is null) throw new ApiException(409, "no_matching_image", "No release image matches the engine target.") {
                Details = new JsonObject { ["retryable"] = false, ["os"] = platform["os"]!.DeepClone(), ["architecture"] = platform["architecture"]!.DeepClone() } };
            AppConfigurationRevision configuration;
            if (kind == "rollback")
            {
                var configId = Guid.Parse(body["configuration_revision_id"]!.GetValue<string>());
                configuration = await db.AppConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == configId && x.InstallationId == installation.Id && x.RevisionId == revisionId, ct)
                    ?? throw ApiException.NotFound("Configuration is not accessible.");
                var applied = await db.AppOperations.AsNoTracking().Where(x => x.InstallationId == installation.Id && x.State == "succeeded" && x.Phase == "applied" && x.Command != null).Select(x => x.Command!).ToListAsync(ct);
                if (!applied.Any(x => JsonNode.Parse(x)!["release"]!["configuration_revision_id"]!.GetValue<string>() == configId.ToString()))
                    throw ApiException.Conflict("stale_installation", "Rollback requires a previously applied retained configuration.");
                ValidateConfiguration(manifest, JsonNode.Parse(configuration.Values)!);
            }
            else
            {
                var values = ValidateConfiguration(manifest, body["configuration"]!);
                configuration = new AppConfigurationRevision { Id = Guid.NewGuid(), InstallationId = installation.Id, AppId = installation.AppId,
                    RevisionId = revision.Id, Values = values.ToJsonString(), CreatedByUserId = user.Id, CreatedAt = now };
                db.AppConfigurationRevisions.Add(configuration);
            }
            Guid? runtimeSettingsRevisionId = installation.SelectedSettingsRevisionId;
            if (kind is "update" or "rollback")
            {
                var selected = runtimeSettingsRevisionId is Guid selectedId
                    ? await db.AppSettingsRevisions.SingleAsync(x => x.Id == selectedId && x.InstallationId == installation.Id, ct)
                    : null;
                AppSettingsRevision? targetSettings = null;
                if (body.ContainsKey("runtime_settings"))
                {
                    if (body["runtime_settings"] is not null)
                    {
                        var values = AppManifest.ValidateRuntimeSettings(manifest, body["runtime_settings"]!);
                        targetSettings = new AppSettingsRevision { Id = Guid.NewGuid(), InstallationId = installation.Id,
                            AppId = installation.AppId, RevisionId = revision.Id, Values = values.ToJsonString(),
                            CreatedByUserId = user.Id, CreatedAt = now };
                        db.AppSettingsRevisions.Add(targetSettings);
                    }
                }
                else if (manifest["runtime_settings"] is JsonObject)
                {
                    if (kind == "rollback")
                        targetSettings = await db.AppSettingsRevisions.Where(x => x.InstallationId == installation.Id && x.RevisionId == revision.Id)
                            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
                    targetSettings ??= selected;
                    if (targetSettings is not null)
                    {
                        JsonObject values;
                        try { values = AppManifest.ValidateRuntimeSettings(manifest, JsonNode.Parse(targetSettings.Values)!); }
                        catch (ApiException)
                        {
                            throw ApiException.Conflict("stale_settings_selection", "Current settings do not satisfy the target release. Supply corrected runtime_settings values.");
                        }
                        if (targetSettings.RevisionId != revision.Id)
                        {
                            targetSettings = new AppSettingsRevision { Id = Guid.NewGuid(), InstallationId = installation.Id,
                                AppId = installation.AppId, RevisionId = revision.Id, Values = values.ToJsonString(),
                                CreatedByUserId = user.Id, CreatedAt = now };
                            db.AppSettingsRevisions.Add(targetSettings);
                        }
                    }
                }
                else if (selected is not null)
                {
                    throw ApiException.Conflict("stale_settings_selection", "The target release does not support live settings. Supply runtime_settings: null to clear the current selection.");
                }
                runtimeSettingsRevisionId = targetSettings?.Id;
            }
            var releaseCommand = new JsonObject { ["app_id"] = installation.AppId.ToString(), ["revision_id"] = revision.Id.ToString(),
                ["manifest_digest"] = revision.ManifestDigest, ["configuration_revision_id"] = configuration.Id.ToString(),
                ["configuration"] = JsonNode.Parse(configuration.Values), ["target"] = target.DeepClone() };
            if (inventory.ManifestV2Supported)
                releaseCommand["runtime_settings_revision_id"] = runtimeSettingsRevisionId?.ToString();
            else if (runtimeSettingsRevisionId != installation.SelectedSettingsRevisionId)
                throw ApiException.Conflict("daemon_upgrade_required", "The current Daemon must negotiate manifest v2 before changing this settings selection.");
            var operation = new AppOperation { Id = Guid.NewGuid(), OrganizationId = robot.OrganizationId, RobotId = robot.Id, InstallationId = installation.Id,
                Kind = kind, State = "queued", Phase = "queued", RequestedByUserId = user.Id, RequestedAt = now, DeadlineAt = now.AddMinutes(10) };
            var command = new JsonObject { ["app_protocol_version"] = 1, ["deployment_namespace"] = inventory.DeploymentNamespace.ToString(), ["installation_id"] = installation.Id.ToString(),
                ["expected_revision_id"] = installed?["revision_id"]?.DeepClone(), ["expected_configuration_revision_id"] = installed?["configuration_revision_id"]?.DeepClone(),
                ["release"] = releaseCommand };
            AppProtocol.Validate(command.ToJsonString(), "apply_command");
            operation.Command = command.ToJsonString(); db.AppOperations.Add(operation);
            installation.AvailableRevisionId = revision.Id;
            installation.Candidate = new JsonObject { ["operation_id"] = operation.Id.ToString(), ["revision_id"] = revision.Id.ToString(), ["configuration_revision_id"] = configuration.Id.ToString(),
                ["image"] = target["image"]!.DeepClone(), ["state"] = "staging", ["requested_at"] = AppProtocol.Timestamp(now), ["observed_at"] = null, ["staged_at"] = null, ["failure"] = null }.ToJsonString();
            return new JsonObject { ["installation_id"] = installation.Id.ToString(), ["operation_id"] = operation.Id.ToString(), ["run_id"] = null };
        }, ct);
    }

    internal static JsonObject InstallationJson(AppInstallation i) => new() { ["id"] = i.Id.ToString(), ["organization_id"] = i.OrganizationId.ToString(), ["robot_id"] = i.RobotId.ToString(),
        ["app_id"] = i.AppId.ToString(), ["created_by"] = i.CreatedByUserId.ToString(), ["created_at"] = AppProtocol.Timestamp(i.CreatedAt), ["available_revision_id"] = i.AvailableRevisionId?.ToString(),
        ["installed"] = i.InstalledBinding is null ? null : JsonNode.Parse(i.InstalledBinding), ["candidate"] = i.Candidate is null ? null : JsonNode.Parse(i.Candidate), ["last_observed_at"] = Stamp(i.LastObservedAt) };
    internal static string? Stamp(DateTimeOffset? value) => value is null ? null : AppProtocol.Timestamp(value.Value);
    internal static JsonObject OperationJson(AppOperation o) => new() { ["id"] = o.Id.ToString(), ["organization_id"] = o.OrganizationId.ToString(), ["robot_id"] = o.RobotId.ToString(),
        ["installation_id"] = o.InstallationId.ToString(), ["run_id"] = o.RunId?.ToString(), ["kind"] = o.Kind, ["state"] = o.State, ["phase"] = o.Phase,
        ["requested_by"] = o.RequestedByUserId.ToString(), ["requested_at"] = AppProtocol.Timestamp(o.RequestedAt), ["deadline_at"] = AppProtocol.Timestamp(o.DeadlineAt),
        ["acknowledged_at"] = Stamp(o.AcknowledgedAt), ["observed_at"] = Stamp(o.ObservedAt), ["received_at"] = Stamp(o.ReceivedAt), ["completed_at"] = Stamp(o.CompletedAt), ["failure"] = o.Failure is null ? null : JsonNode.Parse(o.Failure) };
    private static async Task<AppInstallation> Read(Guid id, HttpContext c, RingfourDbContext db, CancellationToken ct)
    { var i = await db.AppInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Installation is not accessible."); await RobotAccess.RequireRobotAccess(c, db, i.RobotId, OrgRole.Operator, ct); return i; }
    private static async Task<IResult> Get(Guid installationId, HttpContext c, RingfourDbContext db, CancellationToken ct) => Results.Json(InstallationJson(await Read(installationId, c, db, ct)));
    private static async Task<IResult> List(Guid robotId, string? cursor, int? limit, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await RobotAccess.RequireRobotAccess(c, db, robotId, OrgRole.Operator, ct);
        if (limit is < 1 or > 100 || cursor?.Length > 1024) throw ApiException.InvalidInput("Invalid page limit/cursor.");
        var (at,id) = Pagination.DecodeCursor(cursor); var size = limit ?? 50;
        var query = db.AppInstallations.AsNoTracking().Where(x => x.RobotId == robotId);
        if (at is not null) query = query.Where(x => x.CreatedAt > at || (x.CreatedAt == at && x.Id.CompareTo(id!.Value) > 0));
        var rows = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(size + 1).ToListAsync(ct); var page = rows.Take(size).ToList();
        return Results.Json(new { items = page.Select(InstallationJson), next_cursor = Pagination.NextCursor(rows.Count > size, page, x => x.CreatedAt, x => x.Id) });
    }
    private static async Task<IResult> Operation(Guid operationId, HttpContext c, RingfourDbContext db, CancellationToken ct)
    { var o = await db.AppOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct) ?? throw ApiException.NotFound("Operation is not accessible."); await Read(o.InstallationId, c, db, ct); return Results.Json(OperationJson(o)); }
    private static async Task<IResult> Configuration(Guid installationId, Guid configurationRevisionId, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await Read(installationId, c, db, ct);
        var r = await db.AppConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == configurationRevisionId && x.InstallationId == installationId, ct) ?? throw ApiException.NotFound("Configuration is not accessible.");
        return Results.Json(new { id = r.Id, installation_id = r.InstallationId, revision_id = r.RevisionId, values = JsonNode.Parse(r.Values), created_by = r.CreatedByUserId, created_at = AppProtocol.Timestamp(r.CreatedAt) });
    }
}
