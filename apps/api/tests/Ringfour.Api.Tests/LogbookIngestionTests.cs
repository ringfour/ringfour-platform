using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LogbookIngestionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MotionMigrationPreservesExistingHistory()
    {
        var database = $"motion_migration_{Guid.NewGuid():N}";
        await postgres.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
            var options = new DbContextOptionsBuilder<RingfourDbContext>().UseNpgsql(connection).Options;
            await using var db = new RingfourDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261113144817_LogbookHistoryIndex");
            var org = Guid.NewGuid(); var robot = Guid.NewGuid(); var operation = Guid.NewGuid(); var entry = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations (id,name,created_at) VALUES ({org},{"migration test"},{DateTimeOffset.UtcNow})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO robots (id,organization_id,name,created_at) VALUES ({robot},{org},{"robot"},{DateTimeOffset.UtcNow})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations (id,robot_id,kind,payload_version,payload,state,created_at,updated_at,deadline_at) VALUES ({operation},{robot},{"fixture.test"},{1},{"{}"}::jsonb,{"Queued"},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO logbook_events (id,robot_id,operation_id,local_sequence,type,schema_version,occurred_at,received_at,state) VALUES ({entry},{robot},{operation},{1L},{"device.operation.state_changed"},{1},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow},{"accepted"})");
            await migrator.MigrateAsync();
            var preserved = await db.LogbookEvents.SingleAsync(item => item.Id == entry);
            Assert.Equal(operation, preserved.OperationId);
            Assert.Equal("accepted", preserved.State);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.ExecuteAsync($"DROP DATABASE \"{database}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task AgentMotionIsRobotScopedIdempotentAndDoesNotInventHumanAttribution()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "motion history");
        var robot = await RobotTests.CreateRobot(owner, org, "motion robot");
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credential.ToString());
        device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
        var id = Guid.NewGuid().ToString();
        JsonObject item = new() {
            ["event_id"] = id, ["robot_id"] = robot.ToString(), ["local_sequence"] = 1,
            ["occurred_at"] = "2026-11-13T12:00:00.000000Z", ["type"] = "agent.motion.event",
            ["schema_version"] = 1, ["operation_id"] = null,
            ["payload"] = new JsonObject { ["event_id"] = id, ["stream_id"] = Guid.NewGuid().ToString(),
                ["sequence"] = 1, ["occurred_at"] = "2026-11-13T12:00:00Z", ["event_type"] = "motion.stop_verified",
                ["operation_id"] = "task-1", ["session_id"] = null, ["owner"] = "unix:uid:1000", ["stop_verified"] = true }
        };
        static JsonObject Batch(JsonObject input) => new() { ["events"] = new JsonArray(input.DeepClone()) };
        const string url = "/v1/device/logbook/events";
        var falseStop = (JsonObject)item.DeepClone(); falseStop["payload"]!["stop_verified"] = false;
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(url, Batch(falseStop))).StatusCode);
        var forged = (JsonObject)item.DeepClone(); forged["payload"]!["event_id"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(url, Batch(forged))).StatusCode);
        var otherRobot = (JsonObject)item.DeepClone(); otherRobot["robot_id"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(otherRobot))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(url, Batch(item))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(url, Batch(item))).StatusCode);
        var changed = (JsonObject)item.DeepClone(); changed["payload"]!["owner"] = "different";
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(changed))).StatusCode);
        var gap = (JsonObject)item.DeepClone(); var gapId = Guid.NewGuid().ToString();
        gap["event_id"] = gapId; gap["local_sequence"] = 2;
        gap["payload"]!["event_id"] = gapId; gap["payload"]!["sequence"] = 2;
        gap["payload"]!["event_type"] = "motion.history_incomplete"; gap["payload"]!["stop_verified"] = null;
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(url, Batch(gap))).StatusCode);
        var historyUrl = $"/v1/organizations/{org}/app-logbook?robot_id={robot}";
        var history = (await owner.GetFromJsonAsync<JsonObject>(historyUrl))!["items"]!.AsArray();
        Assert.Equal(2, history.Count);
        Assert.Null(history[0]!["operation_id"]);
        Assert.Null(history[0]!["run_id"]);
        Assert.Null(history[0]!["actor_user_id"]);
        Assert.Equal("motion.stop_verified", history[0]!["payload"]!["event_type"]!.GetValue<string>());
        Assert.Equal("motion.history_incomplete", history[1]!["payload"]!["event_type"]!.GetValue<string>());
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(historyUrl)).StatusCode);
    }

}
