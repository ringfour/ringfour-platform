using System.Text.Json;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Logbook;

public static class MissionLifecycleLogbook
{
    public static LogbookEvent? Create(ScriptExecution execution, Guid eventId,
        string state, DateTimeOffset occurredAt)
    {
        var type = state switch
        {
            "queued" => "mission.revision_selected",
            "running" => "mission.started",
            "succeeded" => "mission.completed",
            "failed" => "mission.failed",
            "cancelled" => "mission.stopped",
            _ => null,
        };
        if (type is null) return null;
        var payload = JsonSerializer.Serialize(new
        {
            state,
            revision_id = execution.RevisionId,
            mission_revision_id = execution.MissionRevisionId,
        }, RingfourJson.Options);
        return new LogbookEvent
        {
            Id = eventId,
            RobotId = execution.RobotId,
            OperationId = execution.Id,
            LocalSequence = null,
            Type = type,
            SchemaVersion = 1,
            OccurredAt = occurredAt,
            ReceivedAt = occurredAt,
            ExecutionId = execution.Id,
            ActorUserId = execution.ActorUserId,
            Payload = payload,
        };
    }

    public static void Record(RingfourDbContext db, ScriptExecution execution, Guid eventId,
        string state, DateTimeOffset occurredAt)
    {
        var entry = Create(execution, eventId, state, occurredAt);
        if (entry is not null) db.LogbookEvents.Add(entry);
    }
}
