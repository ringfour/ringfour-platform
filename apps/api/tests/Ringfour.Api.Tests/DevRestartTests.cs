using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DevRestartTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RestartAndStatusAreScopedAndDoNotStoreOutput()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "dev restart org");
        var robot = await RobotTests.CreateRobot(owner, org, "robot");
        var ownerId = await RobotTests.UserId(owner);
        var session = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO dev_sessions (id, robot_id, owner_user_id, state, generation, created_at, updated_at, expires_at)
            VALUES (@id, @robot, @owner, 'ready', 21, now(), now(), now() + interval '1 hour')
            """, ("id", session), ("robot", robot), ("owner", ownerId));
        // Retained terminal history must not prevent a later generation from starting.
        await postgres.ExecuteAsync("""
            WITH history AS (
                INSERT INTO operations (id, robot_id, kind, payload_version, payload, state, created_at, updated_at, deadline_at)
                SELECT gen_random_uuid(), @robot, 'dev.session.restart', 1, '{}', 'Succeeded', now(), now(), now() + interval '1 hour'
                FROM generate_series(1, 100) RETURNING id
            )
            INSERT INTO dev_session_runs (id, session_id, robot_id, actor_user_id, operation_id, generation, state, exit_code, created_at, updated_at)
            SELECT gen_random_uuid(), @session, @robot, @owner, id, 21, 'succeeded', 0, now(), now() FROM history
            """, ("robot", robot), ("session", session), ("owner", ownerId));
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        using var socket = await DeviceSocket.ConnectAsync(factory, robot, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket);
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "restart-test");
        outsider.DefaultRequestHeaders.Add("Idempotency-Key", "other");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/v1/dev-sessions/{session}/restart", new { generation = 21 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/restart", new { generation = 22 })).StatusCode);
        var accepted = await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/restart", new { generation = 21 });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadAsStringAsync();
        ContractSchema.AssertValid("dev-sessions/v1/accepted.schema.json", body);
        var response = JsonDocument.Parse(body).RootElement;
        var operation = response.GetProperty("operation_id").GetGuid();
        var execution = response.GetProperty("execution_id").GetGuid();
        Assert.Equal(body, await (await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/restart", new { generation = 21 })).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/restart", new { generation = 22 })).StatusCode);
        using var command = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("dev.session.restart", command.Payload().GetProperty("kind").GetString());
        ContractSchema.AssertValid("dev-sessions/v1/runner-restart.schema.json", command.Payload().GetProperty("payload").GetRawText());
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
        device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
        var path = $"/v1/device/dev-sessions/{session}/runs/{execution}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync(path, new { state = "running", exit_code = (int?)null, error = (object?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PutAsJsonAsync(path, new { state = "running", exit_code = (int?)null, error = (object?)null, stdout = "private" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync(path, new { state = "running", exit_code = (int?)null, error = (object?)null })).StatusCode);
        var state = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
        Assert.Equal("running", state.GetProperty("state").GetString());
        Assert.Equal(21, state.GetProperty("active_generation").GetInt32());
        await DeviceSocket.SendAsync(socket, robot, 2, "ack", new JsonObject { ["operation_id"] = operation.ToString() });
        await DeviceSocket.SendAsync(socket, robot, 3, "operation_status", new JsonObject { ["operation_id"] = operation.ToString(), ["state"] = "succeeded", ["result"] = new JsonObject() });
        Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync(path, new { state = "succeeded", exit_code = 0, error = (object?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync(path, new { state = "succeeded", exit_code = 0, error = (object?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PutAsJsonAsync(path, new { state = "running", exit_code = (int?)null, error = (object?)null })).StatusCode);
        state = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
        Assert.Equal("ready", state.GetProperty("state").GetString());
        var rows = await postgres.QueryAsync("SELECT state, error FROM dev_session_runs WHERE id=@id", ("id", execution));
        Assert.Equal("succeeded", rows[0][0]);
        owner.DefaultRequestHeaders.Remove("Idempotency-Key");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "low-storage");
        var failedStart = await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/restart", new { generation = 21 });
        Assert.Equal(HttpStatusCode.Accepted, failedStart.StatusCode);
        var failedExecution = JsonDocument.Parse(await failedStart.Content.ReadAsStringAsync()).RootElement.GetProperty("execution_id").GetGuid();
        using var failedCommand = await DeviceSocket.ReceiveAsync(socket);
        var failedOperation = failedCommand.Payload().GetProperty("operation_id").GetGuid();
        await DeviceSocket.SendAsync(socket, robot, 4, "operation_status", new JsonObject {
            ["operation_id"] = failedOperation.ToString(), ["state"] = "failed",
            ["result"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "insufficient_storage", ["message"] = "Runner rejected the operation." } }
        });
        List<object?[]> failure = [];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            failure = await postgres.QueryAsync("SELECT state,error->>'code' FROM dev_session_runs WHERE id=@id", ("id", failedExecution));
            if ((string)failure[0][0]! == "failed") break;
            await Task.Delay(50);
        }
        Assert.Equal("failed", failure[0][0]);
        Assert.Equal("insufficient_storage", failure[0][1]);

    }
}
