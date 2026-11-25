using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

internal static class AppRunDevice
{
    private static ApiException Invalid() => ApiException.Conflict("invalid_observation", "Observation does not match durable run intent.");
    internal static async Task Observe(RingfourDbContext db, Guid robot, JsonObject report, DateTimeOffset received, CancellationToken ct)
    {
        var observations = report["runs"]!.AsArray(); var operations = report["operations"]!.AsArray().Where(x => x!["kind"]!.GetValue<string>() is "start" or "stop" or "settings.apply").ToList();
        if (observations.Count == 0 && operations.Count == 0) return;
        if (report["installations"]!.AsArray().Count != 0 || observations.Select(x => x!["run_id"]!.ToString()).Distinct().Count() != observations.Count) throw Invalid();
        var runs = new Dictionary<Guid, AppRun>();
        foreach (var item in observations)
        {
            var id = Guid.Parse(item!["run_id"]!.GetValue<string>());
            var run = await db.AppRuns.SingleOrDefaultAsync(x => x.Id == id && x.RobotId == robot, ct) ?? throw Invalid();
            if (item["installation_id"]!.GetValue<string>() != run.InstallationId.ToString()) throw Invalid();
            var observed = DateTimeOffset.Parse(item["observed_at"]!.GetValue<string>()); var state = item["state"]!.GetValue<string>();
            DateTimeOffset? Stamp(string key) => item[key] is null ? null : DateTimeOffset.Parse(item[key]!.GetValue<string>());
            var started = Stamp("started_at"); var ended = Stamp("ended_at"); var exit = item["exit_code"]?.GetValue<int>(); var failure = item["failure"]?.ToJsonString();
            if (observed > received.AddMinutes(5) || started > observed || ended > observed || (ended is not null && started > ended) ||
                (run.StartedAt is not null && run.StartedAt != started) || (run.ObservedAt is not null && observed < run.ObservedAt)) throw Invalid();
            if (!AppRunEndpoints.Active.Contains(run.State))
            {
                if (run.State != state || run.StartedAt != started || run.EndedAt != ended || run.ExitCode != exit || !JsonNode.DeepEquals(run.Failure is null ? null : JsonNode.Parse(run.Failure), item["failure"])) throw Invalid();
            }
            else
            {
                var firstStart = run.StartedAt is null && started is not null;
                var changedState = run.State != state;
                if (run.State == "running" && state == "accepted") throw Invalid();
                run.State = state; run.StartedAt = started; run.EndedAt = ended; run.ExitCode = exit; run.Failure = failure;
                if (!AppRunEndpoints.Active.Contains(state)) run.CompletedAt = received;
                run.ObservedAt = observed; run.ReceivedAt = received;
                if (firstStart) AppHistoryEndpoints.Event(db,run,"app.run.started",started!.Value,received);
                if (changedState && !AppRunEndpoints.Active.Contains(state))
                    AppHistoryEndpoints.Event(db,run,"app.run." + (state=="succeeded"?"completed":state),ended??observed,received);
                if (changedState && !AppRunEndpoints.Active.Contains(state))
                {
                    var unresolved = await db.AppSettingsApplications.Where(x => x.RunId == run.Id && x.State == "pending").ToListAsync(ct);
                    foreach (var application in unresolved)
                    {
                        application.State = "ended_before_confirmation";
                        application.CompletedAt = received;
                        AppHistoryEndpoints.SettingsEvent(db, robot, run.InstallationId, application.ApplicationId, application.ApplicationId,
                            run.Id, application.RequestedByUserId, "app.settings.ended_before_confirmation",
                            new JsonObject { ["application_id"] = application.ApplicationId.ToString(),
                                ["settings_revision_id"] = application.SettingsRevisionId.ToString(), ["state"] = "ended_before_confirmation" }, received);
                    }
                }
            }
            runs.Add(id,run);
        }
        foreach (var item in operations)
        {
            var operation = await db.AppOperations.SingleOrDefaultAsync(x => x.Id == Guid.Parse(item!["operation_id"]!.GetValue<string>()) && x.RobotId == robot, ct) ?? throw Invalid();
            if (operation.Command is null || operation.RunId is null || !runs.TryGetValue(operation.RunId.Value, out var run) || operation.Kind != item!["kind"]!.GetValue<string>() ||
                item["run_id"]!.GetValue<string>() != run.Id.ToString() || item["installation_id"]!.GetValue<string>() != run.InstallationId.ToString()) throw Invalid();
            var state = item["state"]!.GetValue<string>(); var phase = item["phase"]!.GetValue<string>(); var observed = DateTimeOffset.Parse(item["observed_at"]!.GetValue<string>());
            var wasPending = AppInstallationEndpoints.Pending.Contains(operation.State);
            AppSettingsApplication? settingsApplication = null;
            if (operation.Kind == "settings.apply")
            {
                var result = item["settings_application"]?.AsObject() ?? throw Invalid();
                var applicationId = operation.Id;
                var revisionId = Guid.Parse(result["settings_revision_id"]!.GetValue<string>());
                if (Guid.Parse(result["application_id"]!.GetValue<string>()) != applicationId || operation.Id != Guid.Parse(item["operation_id"]!.GetValue<string>())) throw Invalid();
                settingsApplication = await db.AppSettingsApplications.SingleOrDefaultAsync(x => x.ApplicationId == applicationId && x.RunId == run.Id && x.InstallationId == run.InstallationId, ct) ?? throw Invalid();
                var outcome = result["outcome"]!.GetValue<string>(); var reason = result["reason"]?.GetValue<string>();
                if (settingsApplication.SettingsRevisionId != revisionId || (outcome == "applied" && (state != "succeeded" || phase != "applied" || reason is not null)) ||
                    (outcome == "rejected" && (state != "failed" || phase != "failed" || reason is null || reason.Length > 240 || reason.Any(char.IsControl)))) throw Invalid();
                if (state == "succeeded")
                {
                    run.AppliedSettingsRevisionId = revisionId;
                    settingsApplication.State = "applied"; settingsApplication.Outcome = "applied"; settingsApplication.Reason = null;
                    var installation = await db.AppInstallations.SingleAsync(x => x.Id == run.InstallationId, ct);
                    if (installation.SelectedSettingsRevisionId == settingsApplication.ExpectedSelectedRevisionId)
                        installation.SelectedSettingsRevisionId = revisionId;
                }
                else
                {
                    settingsApplication.State = "rejected"; settingsApplication.Outcome = "rejected"; settingsApplication.Reason = reason;
                }
                settingsApplication.DeliveryState = "reported"; settingsApplication.ReportedAt = received; settingsApplication.CompletedAt = received;
                if (wasPending)
                    AppHistoryEndpoints.SettingsEvent(db, robot, run.InstallationId, applicationId, applicationId, run.Id,
                        settingsApplication.RequestedByUserId, state == "succeeded" ? "app.settings.applied" : "app.settings.rejected",
                        new JsonObject { ["application_id"] = applicationId.ToString(), ["settings_revision_id"] = revisionId.ToString(), ["outcome"] = outcome, ["reason"] = reason }, received);
            }
            else
            {
                if (state == "succeeded" && (operation.Kind == "start" ? (phase != "observed_running" || run.StartedAt is null) : (phase != "stopped" || AppRunEndpoints.Active.Contains(run.State)))) throw Invalid();
                if (state == "failed" && AppRunEndpoints.Active.Contains(run.State)) throw Invalid();
            }
            if (!AppInstallationEndpoints.Pending.Contains(operation.State))
            {
                if (operation.State != state || operation.Phase != phase || operation.ObservedAt != observed || !JsonNode.DeepEquals(operation.Failure is null ? null : JsonNode.Parse(operation.Failure), item["failure"])) throw Invalid();
                continue;
            }
            operation.State = state; operation.Phase = phase; operation.ObservedAt = observed; operation.ReceivedAt = received;
            operation.AcknowledgedAt ??= received; operation.Failure = item["failure"]?.ToJsonString();
            if (!AppInstallationEndpoints.Pending.Contains(state)) operation.CompletedAt = received;
        }
    }
}
