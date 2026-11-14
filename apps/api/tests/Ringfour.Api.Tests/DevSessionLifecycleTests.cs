using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DevSessionLifecycleTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CreationAndClosureRequireDeviceConfirmation()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "dev session lifecycle");
        var robot = await RobotTests.CreateRobot(owner, org, "dev robot");
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "create");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/dev-sessions", new { ttl_seconds = 3600 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/dev-sessions", new { ttl_seconds = 299 })).StatusCode);
        using var socket = await DeviceSocket.ConnectAsync(factory, robot, credential, secret);
        await DeviceSocket.ReceiveAsync(socket);
        var accepted = await owner.PostAsJsonAsync($"/v1/robots/{robot}/dev-sessions", new { ttl_seconds = 3600 });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadAsStringAsync();
        ContractSchema.AssertValid("dev-sessions/v1/accepted.schema.json", body);
        var json = JsonDocument.Parse(body).RootElement;
        var session = json.GetProperty("session_id").GetGuid();
        var operation = json.GetProperty("operation_id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/dev-sessions/{session}")).StatusCode);
        var replay = await owner.PostAsJsonAsync($"/v1/robots/{robot}/dev-sessions", new { ttl_seconds = 3600 });
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/dev-sessions", new { ttl_seconds = 500 })).StatusCode);
        owner.DefaultRequestHeaders.Remove("Idempotency-Key");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "other");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/dev-sessions", new { ttl_seconds = 3600 })).StatusCode);
        using var createCommand = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("dev.session.create", createCommand.Payload().GetProperty("kind").GetString());
        ContractSchema.AssertValid("dev-sessions/v1/lifecycle-command.schema.json", createCommand.Payload().GetProperty("payload").GetRawText());
        await DeviceSocket.SendAsync(socket, robot, 2, "ack", new JsonObject { ["operation_id"] = operation.ToString() });
        await DeviceSocket.SendAsync(socket, robot, 3, "operation_status", new JsonObject { ["operation_id"] = operation.ToString(), ["state"] = "succeeded", ["result"] = new JsonObject { ["session_id"] = session.ToString(), ["state"] = "ready", ["generation"] = 0 } });
        JsonElement state = default;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            state = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
            if (state.GetProperty("state").GetString() == "ready") break;
            await Task.Delay(50);
        }
        ContractSchema.AssertValid("dev-sessions/v1/session.schema.json", state.GetRawText());
        Assert.Equal("ready", state.GetProperty("state").GetString());
        owner.DefaultRequestHeaders.Remove("Idempotency-Key");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "close");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/v1/dev-sessions/{session}")).StatusCode);
        var close = await owner.DeleteAsync($"/v1/dev-sessions/{session}");
        Assert.Equal(HttpStatusCode.Accepted, close.StatusCode);
        var closeText = await close.Content.ReadAsStringAsync();
        Assert.Equal(closeText, await (await owner.DeleteAsync($"/v1/dev-sessions/{session}")).Content.ReadAsStringAsync());
        var closeOp = JsonDocument.Parse(closeText).RootElement.GetProperty("operation_id").GetGuid();
        state = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
        Assert.Equal("closing", state.GetProperty("state").GetString());
        using var closeCommand = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal(closeOp, closeCommand.Payload().GetProperty("operation_id").GetGuid());
        await DeviceSocket.SendAsync(socket, robot, 4, "ack", new JsonObject { ["operation_id"] = closeOp.ToString() });
        await DeviceSocket.SendAsync(socket, robot, 5, "operation_status", new JsonObject { ["operation_id"] = closeOp.ToString(), ["state"] = "succeeded", ["result"] = new JsonObject { ["session_id"] = session.ToString(), ["state"] = "closed", ["generation"] = 0 } });
        for (var attempt = 0; attempt < 80; attempt++)
        {
            state = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
            if (state.GetProperty("state").GetString() == "closed") break;
            await Task.Delay(50);
        }
        Assert.Equal("closed", state.GetProperty("state").GetString());
    }
    [Fact]
    public async Task ExpiredSessionWaitsForDeviceCleanup()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "expired session org");
        var robot = await RobotTests.CreateRobot(owner, org, "robot");
        var ownerId = await RobotTests.UserId(owner);
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        using var socket = await DeviceSocket.ConnectAsync(factory, robot, credential, secret);
        await DeviceSocket.ReceiveAsync(socket);
        var session = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO dev_sessions (id, robot_id, owner_user_id, state, generation, created_at, updated_at, expires_at)
            VALUES (@id, @robot, @owner, 'ready', 0, now() - interval '2 hours', now() - interval '2 hours', now() - interval '1 hour')
            """, ("id", session), ("robot", robot), ("owner", ownerId));
        using var command = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("dev.session.expire", command.Payload().GetProperty("kind").GetString());
        var op = command.Payload().GetProperty("operation_id").GetGuid();
        var pending = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
        Assert.Equal("closing", pending.GetProperty("state").GetString());
        await DeviceSocket.SendAsync(socket, robot, 2, "ack", new JsonObject { ["operation_id"] = op.ToString() });
        await DeviceSocket.SendAsync(socket, robot, 3, "operation_status", new JsonObject { ["operation_id"] = op.ToString(), ["state"] = "succeeded", ["result"] = new JsonObject { ["session_id"] = session.ToString(), ["state"] = "closed", ["generation"] = 0 } });
        for (var attempt = 0; attempt < 80; attempt++)
        {
            pending = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
            if (pending.GetProperty("state").GetString() == "expired") break;
            await Task.Delay(50);
        }
        Assert.Equal("expired", pending.GetProperty("state").GetString());
    }

}
