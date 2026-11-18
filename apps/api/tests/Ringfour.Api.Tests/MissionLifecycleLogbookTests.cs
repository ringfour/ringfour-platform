using System.Text.Json;
using Ringfour.Api.Logbook;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

public sealed class MissionLifecycleLogbookTests
{
    [Theory]
    [InlineData("queued", "mission.revision_selected")]
    [InlineData("running", "mission.started")]
    [InlineData("succeeded", "mission.completed")]
    [InlineData("failed", "mission.failed")]
    [InlineData("cancelled", "mission.stopped")]
    public void Lifecycle_entries_keep_execution_and_actor_attribution(string state, string type)
    {
        var id = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var robotId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var missionRevisionId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.Parse("2026-11-17T10:11:12Z");
        var execution = new ScriptExecution
        {
            Id = id,
            RobotId = robotId,
            RevisionId = revisionId,
            MissionRevisionId = missionRevisionId,
            ActorUserId = actorId,
            State = state,
            CreatedAt = occurredAt,
            UpdatedAt = occurredAt,
        };

        var entry = MissionLifecycleLogbook.Create(execution, id, state, occurredAt);

        Assert.NotNull(entry);
        Assert.Equal(id, entry.Id);
        Assert.Equal(robotId, entry.RobotId);
        Assert.Equal(id, entry.OperationId);
        Assert.Null(entry.LocalSequence);
        Assert.Equal(type, entry.Type);
        Assert.Equal(id, entry.ExecutionId);
        Assert.Equal(actorId, entry.ActorUserId);
        Assert.Equal(occurredAt, entry.OccurredAt);
        using var payload = JsonDocument.Parse(entry.Payload!);
        Assert.Equal(state, payload.RootElement.GetProperty("state").GetString());
        Assert.Equal(revisionId, payload.RootElement.GetProperty("revision_id").GetGuid());
        Assert.Equal(missionRevisionId, payload.RootElement.GetProperty("mission_revision_id").GetGuid());
    }

    [Fact]
    public void Intermediate_preparing_state_does_not_create_a_mission_lifecycle_entry()
    {
        var execution = new ScriptExecution
        {
            Id = Guid.NewGuid(),
            RobotId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
            State = "preparing",
        };

        Assert.Null(MissionLifecycleLogbook.Create(execution, Guid.NewGuid(), "preparing", DateTimeOffset.UtcNow));
    }
}
