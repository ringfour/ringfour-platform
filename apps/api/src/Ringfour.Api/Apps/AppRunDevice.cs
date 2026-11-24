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
        var observations = report["runs"]!.AsArray(); var operations = report["operations"]!.AsArray().Where(x => x!["kind"]!.GetValue<string>() is "start" or "stop").ToList();
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
            }
            runs.Add(id,run);
        }
        foreach (var item in operations)
        {
            var operation = await db.AppOperations.SingleOrDefaultAsync(x => x.Id == Guid.Parse(item!["operation_id"]!.GetValue<string>()) && x.RobotId == robot, ct) ?? throw Invalid();
            if (operation.Command is null || operation.RunId is null || !runs.TryGetValue(operation.RunId.Value, out var run) || operation.Kind != item!["kind"]!.GetValue<string>() ||
                item["run_id"]!.GetValue<string>() != run.Id.ToString() || item["installation_id"]!.GetValue<string>() != run.InstallationId.ToString()) throw Invalid();
            var state = item["state"]!.GetValue<string>(); var phase = item["phase"]!.GetValue<string>(); var observed = DateTimeOffset.Parse(item["observed_at"]!.GetValue<string>());
            if (state == "succeeded" && (operation.Kind == "start" ? (phase != "observed_running" || run.StartedAt is null) : (phase != "stopped" || AppRunEndpoints.Active.Contains(run.State)))) throw Invalid();
            if (state == "failed" && AppRunEndpoints.Active.Contains(run.State)) throw Invalid();
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
