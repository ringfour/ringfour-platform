using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Apps;

public static class AppSettingsEndpoints
{
    public static void MapAppSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1").RequireAuthorization().WithMetadata(new AppErrorResponse());
        group.MapGet("/app-installations/{installationId:guid}/settings", Get);
        group.MapPost("/app-installations/{installationId:guid}/settings", Save);
        group.MapGet("/app-installations/{installationId:guid}/settings/revisions/{revisionId:guid}", GetRevision);
    }

    private static JsonObject RevisionJson(AppSettingsRevision row) => new()
    {
        ["id"] = row.Id.ToString(), ["installation_id"] = row.InstallationId.ToString(),
        ["revision_id"] = row.RevisionId.ToString(), ["values"] = JsonNode.Parse(row.Values),
        ["created_by"] = row.CreatedByUserId.ToString(), ["created_at"] = AppProtocol.Timestamp(row.CreatedAt)
    };

    private static JsonObject ApplicationJson(AppSettingsApplication row, AppSettingsRevision revision) => new()
    {
        ["application_id"] = row.ApplicationId.ToString(), ["run_id"] = row.RunId.ToString(),
        ["settings_revision_id"] = row.SettingsRevisionId.ToString(), ["values"] = JsonNode.Parse(revision.Values),
        ["state"] = row.State, ["initial"] = row.Initial, ["requested_at"] = AppProtocol.Timestamp(row.RequestedAt),
        ["reason"] = row.Reason, ["delivery_state"] = row.DeliveryState
    };

    private static async Task<(AppInstallation Installation, User User, Robot Robot)> Authorized(
        Guid installationId, HttpContext context, RingfourDbContext db, OrgRole role, CancellationToken ct)
    {
        var installation = await db.AppInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId, ct)
            ?? throw ApiException.NotFound("Installation is not accessible.");
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, installation.RobotId, role, ct);
        return (installation, user, robot);
    }

    private static async Task<IResult> Get(Guid installationId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var (installation, _, robot) = await Authorized(installationId, context, db, OrgRole.Operator, ct);
        var installed = installation.InstalledBinding is null ? null : JsonNode.Parse(installation.InstalledBinding);
        var revisionId = installed is null ? (Guid?)null : Guid.Parse(installed["revision_id"]!.GetValue<string>());
        var manifest = revisionId is null ? null : AppManifest.Validate((await db.AppRevisions.AsNoTracking().SingleAsync(x => x.Id == revisionId, ct)).Manifest);
        var runtimeSettings = manifest?["runtime_settings"]?.DeepClone();
        var inventory = await db.RobotAppInventories.AsNoTracking().SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
        var support = runtimeSettings is null ? "unsupported" :
            inventory is not null && inventory.ManifestV2Supported && inventory.LiveSettingsSupported ? "supported" : "upgrade_required";
        var activeRun = await db.AppRuns.AsNoTracking().Where(x => x.InstallationId == installationId && AppRunEndpoints.Active.Contains(x.State))
            .OrderByDescending(x => x.RequestedAt).FirstOrDefaultAsync(ct);
        var selected = installation.SelectedSettingsRevisionId is Guid selectedId
            ? await db.AppSettingsRevisions.AsNoTracking().SingleAsync(x => x.Id == selectedId && x.InstallationId == installationId, ct)
            : null;
        var applied = activeRun?.AppliedSettingsRevisionId is Guid appliedId
            ? await db.AppSettingsRevisions.AsNoTracking().SingleAsync(x => x.Id == appliedId && x.InstallationId == installationId, ct)
            : null;
        var pending = activeRun is null ? null : await db.AppSettingsApplications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == activeRun.Id && x.State == "pending", ct);
        var pendingRevision = pending is null ? null : await db.AppSettingsRevisions.AsNoTracking()
            .SingleAsync(x => x.Id == pending.SettingsRevisionId && x.InstallationId == installationId, ct);
        var result = new JsonObject
        {
            ["installation_id"] = installationId.ToString(), ["support"] = support,
            ["runtime_settings"] = runtimeSettings,
            ["selected_for_next_start"] = selected is null ? null : RevisionJson(selected),
            ["active_run_id"] = activeRun?.Id.ToString(), ["last_applied"] = applied is null ? null : RevisionJson(applied),
            ["pending_application"] = pending is null ? null : ApplicationJson(pending, pendingRevision!)
        };
        AppProtocol.Validate(result.ToJsonString(), "settings_settings_state");
        return Results.Json(result);
    }

    private static async Task<IResult> GetRevision(Guid installationId, Guid revisionId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        _ = await Authorized(installationId, context, db, OrgRole.Operator, ct);
        var row = await db.AppSettingsRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId && x.InstallationId == installationId, ct)
            ?? throw ApiException.NotFound("Settings revision is not accessible.");
        var revision = RevisionJson(row);
        AppProtocol.Validate(revision.ToJsonString(), "settings_revision");
        return Results.Json(revision);
    }

    private static Guid? GuidValue(JsonNode? value) => value is null ? null : Guid.Parse(value.GetValue<string>());

    private static async Task<IResult> Save(Guid installationId, HttpContext context, RingfourDbContext db,
        DeviceConnectionManager manager, IConfiguration configuration, CancellationToken ct)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (context.Request.Headers["Idempotency-Key"].Count != 1 || key.Length is < 1 or > 200 || key.Any(char.IsControl))
            throw ApiException.InvalidInput("One Idempotency-Key of 1–200 characters is required.");
        var body = await AppProtocol.Read(context.Request, "settings_write", ct);
        var (before, actor, robot) = await Authorized(installationId, context, db, OrgRole.Developer, ct);
        var fingerprint = AppProtocol.Digest(body);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Organizations.FromSqlInterpolated($"SELECT * FROM organizations WHERE id = {robot.OrganizationId} FOR NO KEY UPDATE").SingleAsync(ct);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == robot.OrganizationId && x.UserId == actor.Id, ct);
        if (membership is null) throw ApiException.NotFound("Workspace is not accessible.");
        if (membership.Role < OrgRole.Developer) throw ApiException.Forbidden("This action requires Developer authority.");
        await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {robot.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
        var prior = await db.AppIdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x =>
            x.PrincipalUserId == actor.Id && x.Route == "settings.save" && x.Target == installationId.ToString() && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.RequestFingerprint != fingerprint) throw ApiException.Conflict("idempotency_conflict", "The key was used for different input.");
            var outcome = JsonNode.Parse(prior.Outcome)!;
            context.RequestServices.GetRequiredService<AppDispatchSignal>().Notify();
            return Results.Json(outcome["body"]!.DeepClone(), statusCode: outcome["status"]!.GetValue<int>());
        }

        var installation = await db.AppInstallations.SingleAsync(x => x.Id == installationId, ct);
        var selectedExpected = GuidValue(body["expected_selected_revision_id"]);
        if (selectedExpected != installation.SelectedSettingsRevisionId)
            throw ApiException.Conflict("stale_settings_selection", "The next-start settings selection changed.");
        if (installation.InstalledBinding is null) throw ApiException.Conflict("settings_unsupported", "Install this App before changing its live settings.");
        var binding = JsonNode.Parse(installation.InstalledBinding)!;
        var appRevision = await db.AppRevisions.SingleAsync(x => x.Id == Guid.Parse(binding["revision_id"]!.GetValue<string>()), ct);
        var manifest = AppManifest.Validate(appRevision.Manifest);
        if (manifest["runtime_settings"] is not JsonObject)
            throw ApiException.Conflict("settings_unsupported", "This App release does not declare live settings.");
        var values = AppManifest.ValidateRuntimeSettings(manifest, body["values"]!);
        var inventory = await db.RobotAppInventories.SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
        if (!configuration.GetValue("Apps:LiveSettingsEnabled", false) || inventory is null || !inventory.Supported || !inventory.ManifestV2Supported || !inventory.LiveSettingsSupported)
            throw ApiException.Conflict("daemon_upgrade_required", "A current Daemon session with live-settings support is required.");
        var currentSession = manager.Get(robot.Id);
        if (currentSession is null || currentSession.SessionId != inventory.SessionId || currentSession.Socket.State != System.Net.WebSockets.WebSocketState.Open ||
            !await db.RobotCredentials.AnyAsync(x => x.Id == currentSession.CredentialId && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct))
            throw ApiException.Conflict("daemon_upgrade_required", "A current Daemon session with live-settings support is required.");

        var now = DateTimeOffset.UtcNow;
        var run = await db.AppRuns.Where(x => x.InstallationId == installationId && AppRunEndpoints.Active.Contains(x.State))
            .OrderByDescending(x => x.RequestedAt).FirstOrDefaultAsync(ct);
        var expectedApplied = GuidValue(body["expected_applied_revision_id"]);
        var expectedRun = GuidValue(body["expected_run_id"]);
        if (run is null)
        {
            if (expectedRun is not null || expectedApplied is not null)
                throw ApiException.Conflict("active_run_changed", "The active App run ended or changed.");
        }
        else
        {
            if (expectedRun != run.Id) throw ApiException.Conflict("active_run_changed", "The active App run ended or changed.");
            if (expectedApplied != run.AppliedSettingsRevisionId)
                throw ApiException.Conflict("stale_settings_selection", "The last applied settings revision changed.");
            if (await db.AppSettingsApplications.AnyAsync(x => x.RunId == run.Id && x.State == "pending", ct))
                throw ApiException.Conflict("settings_pending", "A settings application is already unresolved for this run.");
        }

        var settingsRevision = new AppSettingsRevision
        {
            Id = Guid.NewGuid(), InstallationId = installationId, AppId = installation.AppId,
            RevisionId = appRevision.Id, Values = values.ToJsonString(), CreatedByUserId = actor.Id, CreatedAt = now
        };
        db.AppSettingsRevisions.Add(settingsRevision);
        JsonObject response;
        int status;
        if (run is null)
        {
            installation.SelectedSettingsRevisionId = settingsRevision.Id;
            AppHistoryEndpoints.SettingsEvent(db, robot.Id, installationId, settingsRevision.Id, null, null, actor.Id,
                "app.settings.selected", new JsonObject { ["settings_revision_id"] = settingsRevision.Id.ToString(), ["state"] = "selected_for_next_start" }, now);
            response = new JsonObject { ["state"] = "saved_for_next_start", ["settings_revision_id"] = settingsRevision.Id.ToString(), ["selected_revision_id"] = settingsRevision.Id.ToString() };
            status = 200;
            AppProtocol.Validate(response.ToJsonString(), "settings_saved_for_next_start");
        }
        else
        {
            var applicationId = Guid.NewGuid();
            var command = new JsonObject
            {
                ["app_protocol_version"] = 1, ["deployment_namespace"] = inventory.DeploymentNamespace.ToString(),
                ["installation_id"] = installationId.ToString(), ["run_id"] = run.Id.ToString(),
                ["application_id"] = applicationId.ToString(), ["settings_revision_id"] = settingsRevision.Id.ToString(),
                ["expected_applied_revision_id"] = expectedApplied?.ToString(), ["values"] = values.DeepClone(), ["initial"] = false
            };
            AppProtocol.Validate(command.ToJsonString(), "settings_apply_command");
            var operation = new AppOperation
            {
                Id = applicationId, OrganizationId = robot.OrganizationId, RobotId = robot.Id, InstallationId = installationId,
                RunId = run.Id, Kind = "settings.apply", State = "queued", Phase = "queued", Command = command.ToJsonString(),
                RequestedByUserId = actor.Id, RequestedAt = now, DeadlineAt = now.AddMinutes(10)
            };
            db.AppOperations.Add(operation);
            db.AppSettingsApplications.Add(new AppSettingsApplication
            {
                ApplicationId = applicationId, InstallationId = installationId, RunId = run.Id,
                SettingsRevisionId = settingsRevision.Id, ExpectedAppliedRevisionId = expectedApplied,
                ExpectedSelectedRevisionId = selectedExpected, Initial = false, State = "pending", DeliveryState = "queued",
                RequestedByUserId = actor.Id, RequestedAt = now
            });
            AppHistoryEndpoints.SettingsEvent(db, robot.Id, installationId, applicationId, applicationId, run.Id, actor.Id,
                "app.settings.application_requested", new JsonObject
                {
                    ["application_id"] = applicationId.ToString(), ["settings_revision_id"] = settingsRevision.Id.ToString(),
                    ["state"] = "pending", ["initial"] = false
                }, now);
            response = new JsonObject { ["state"] = "pending", ["application_id"] = applicationId.ToString(), ["settings_revision_id"] = settingsRevision.Id.ToString(), ["run_id"] = run.Id.ToString() };
            status = 202;
            AppProtocol.Validate(response.ToJsonString(), "settings_pending_application");
        }
        db.AppIdempotencyRecords.Add(new AppIdempotencyRecord
        {
            Id = Guid.NewGuid(), PrincipalUserId = actor.Id, Route = "settings.save", Target = installationId.ToString(), Key = key,
            RequestFingerprint = fingerprint, Outcome = new JsonObject { ["status"] = status, ["body"] = response.DeepClone() }.ToJsonString(), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (status == 202) context.RequestServices.GetRequiredService<AppDispatchSignal>().Notify();
        return Results.Json(response, statusCode: status);
    }
}
