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
        var historyUrl = $"/v1/organizations/{org}/events?type=agent.motion.event";
        var history = (await owner.GetFromJsonAsync<JsonObject>(historyUrl))!["items"]!.AsArray();
        Assert.Equal(2, history.Count);
        Assert.Null(history[0]!["operation_id"]);
        Assert.Null(history[0]!["execution_id"]);
        Assert.Null(history[0]!["actor_user_id"]);
        Assert.Equal("motion.stop_verified", history[0]!["payload"]!["event_type"]!.GetValue<string>());
        Assert.Equal("motion.history_incomplete", history[1]!["payload"]!["event_type"]!.GetValue<string>());
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(historyUrl)).StatusCode);
    }

    [Fact]
    public async Task IngestionIsAuthenticatedAtomicContiguousAndReplayable()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "logbook test");
        var robot = await RobotTests.CreateRobot(owner, org, "test robot");
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
        device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
        var operation = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO operations (id, robot_id, kind, payload_version, payload, state, created_at, updated_at, deadline_at)
            VALUES (@id, @robot, 'fixture.test', 1, '{}', 'Queued', now(), now(), now() + interval '1 hour')
            """, ("id", operation), ("robot", robot));
        JsonObject Item(long sequence, string state = "accepted") => new() {
            ["event_id"] = Guid.NewGuid().ToString(), ["robot_id"] = robot.ToString(), ["local_sequence"] = sequence,
            ["occurred_at"] = "2026-11-13T12:00:00Z", ["type"] = "device.operation.state_changed",
            ["schema_version"] = 1, ["operation_id"] = operation.ToString(), ["payload"] = new JsonObject { ["state"] = state }
        };
        static JsonObject Batch(params JsonObject[] events) => new() { ["events"] = new JsonArray(events.Select(e => e.DeepClone()).ToArray()) };
        const string url = "/v1/device/logbook/events";
        var first = Item(1); var second = Item(2, "running"); var third = Item(3, "succeeded");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(url, Batch(first))).StatusCode);
        var badCredential = factory.CreateClient();
        badCredential.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
        badCredential.DefaultRequestHeaders.Add("x-ringfour-credential-secret", "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await badCredential.PostAsJsonAsync(url, Batch(first))).StatusCode);
        var wrong = Item(1); wrong["robot_id"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(wrong))).StatusCode);
        var wrongSecond = Item(2); wrongSecond["operation_id"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(first, wrongSecond))).StatusCode);
        Assert.Empty(await postgres.QueryAsync("SELECT id FROM logbook_events WHERE robot_id=@robot", ("robot", robot)));
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(second))).StatusCode);
        var bad = Item(2); bad["schema_version"] = "1";
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(url, Batch(first, bad))).StatusCode);
        Assert.Empty(await postgres.QueryAsync("SELECT id FROM logbook_events WHERE robot_id=@robot", ("robot", robot)));
        var accepted = await device.PostAsJsonAsync(url, Batch(first, second));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, (await accepted.Content.ReadFromJsonAsync<JsonObject>())!["acknowledged_sequence"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(url, Batch(first, second))).StatusCode);
        var conflict = (JsonObject)first.DeepClone(); conflict["payload"]!["state"] = "failed";
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(conflict))).StatusCode);
        var mixed = await device.PostAsJsonAsync(url, Batch(second, third));
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
        Assert.Equal(3, (await mixed.Content.ReadFromJsonAsync<JsonObject>())!["acknowledged_sequence"]!.GetValue<int>());
        var rows = await postgres.QueryAsync("SELECT local_sequence FROM logbook_events WHERE robot_id=@robot ORDER BY local_sequence", ("robot", robot));
        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(row => (long)row[0]!).ToArray());
        var ownerId = await RobotTests.UserId(owner);
        var session = Guid.NewGuid(); var execution = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO dev_sessions (id,robot_id,owner_user_id,state,generation,created_at,updated_at,expires_at)
            VALUES (@session,@robot,@owner,'ready',1,now(),now(),now()+interval '1 hour');
            INSERT INTO dev_session_runs (id,session_id,robot_id,actor_user_id,operation_id,generation,state,created_at,updated_at)
            VALUES (@execution,@session,@robot,@owner,@operation,1,'running',now(),now());
            """, ("session", session), ("robot", robot), ("owner", ownerId), ("execution", execution), ("operation", operation));
        var authored = Item(4);
        authored["type"] = "mission.logbook.entry";
        authored["execution_id"] = execution.ToString();
        authored["payload"] = new JsonObject { ["level"] = "info", ["message"] = "Inspection skipped", ["fields"] = new JsonObject { ["station"] = "B" } };
        var forged = (JsonObject)authored.DeepClone(); forged["execution_id"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(forged))).StatusCode);
        var unsafeText = (JsonObject)authored.DeepClone(); unsafeText["payload"]!["message"] = "invalid\0value";
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(url, Batch(unsafeText))).StatusCode);
        var hugeNumber = (JsonObject)authored.DeepClone(); hugeNumber["payload"]!["fields"]!["value"] = 1e100;
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(url, Batch(hugeNumber))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(url, Batch(authored))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(url, Batch(authored))).StatusCode);
        var changed = (JsonObject)authored.DeepClone(); changed["payload"]!["message"] = "changed";
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(url, Batch(changed))).StatusCode);
        var authorRows = await postgres.QueryAsync("SELECT execution_id,payload->>'message' FROM logbook_events WHERE robot_id=@robot AND local_sequence=4", ("robot", robot));
        Assert.Equal(execution, authorRows[0][0]); Assert.Equal("Inspection skipped", authorRows[0][1]);
        var historyUrl = $"/v1/organizations/{org}/events";
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(historyUrl)).StatusCode);
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(historyUrl)).StatusCode);
        var firstPage = (await owner.GetFromJsonAsync<JsonObject>($"{historyUrl}?limit=2"))!;
        Assert.Equal(2, firstPage["items"]!.AsArray().Count);
        var next = firstPage["next_cursor"]!.GetValue<string>();
        var secondPage = (await owner.GetFromJsonAsync<JsonObject>($"{historyUrl}?limit=2&cursor={Uri.EscapeDataString(next)}"))!;
        Assert.Equal(2, secondPage["items"]!.AsArray().Count);
        Assert.Null(secondPage["next_cursor"]);
        var filtered = (await owner.GetFromJsonAsync<JsonObject>($"{historyUrl}?execution_id={execution}"))!;
        Assert.Single(filtered["items"]!.AsArray());
        Assert.Equal("Inspection skipped", filtered["items"]![0]!["payload"]!["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{historyUrl}?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{historyUrl}?cursor=bad")).StatusCode);
        var oversized = new StringContent(new string('x', 65537), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await device.PostAsync(url, oversized)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/v1/robots/{robot}/credentials/{credentialId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync(url, Batch(third))).StatusCode);
    }
}
