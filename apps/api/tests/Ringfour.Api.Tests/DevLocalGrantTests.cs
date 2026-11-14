using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DevLocalGrantTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DeviceAdvertisementAndScopedGrantAreAuthenticatedAndReplayable()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "local dev grant org");
        var robot = await RobotTests.CreateRobot(owner, org, "test robot");
        var ownerId = await RobotTests.UserId(owner);
        var session = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO dev_sessions (id, robot_id, owner_user_id, state, generation, created_at, updated_at, expires_at)
            VALUES (@id, @robot, @owner, 'ready', 0, now(), now(), now() + interval '1 hour')
            """, ("id", session), ("robot", robot), ("owner", ownerId));
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
        device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
        var certificate = new string('a', 64);
        var endpoint = new { certificate_sha256 = certificate, daemon_url = "https://192.168.1.10:8084" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync("/v1/device/local-dev-endpoint", endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PutAsJsonAsync("/v1/device/local-dev-endpoint", new { certificate_sha256 = certificate, daemon_url = "http://example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync("/v1/device/local-dev-endpoint", endpoint)).StatusCode);
        var grantId = Guid.NewGuid();
        var grant = new { grant_id = grantId, token_sha256 = new string('b', 64) };
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "local-grant");
        outsider.DefaultRequestHeaders.Add("Idempotency-Key", "outsider");
        var otherDeveloper = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var otherId = await RobotTests.UserId(otherDeveloper);
        await postgres.ExecuteAsync("INSERT INTO memberships (organization_id, user_id, role, created_at) VALUES (@org, @user, 'Developer', now())", ("org", org), ("user", otherId));
        otherDeveloper.DefaultRequestHeaders.Add("Idempotency-Key", "other-dev");
        Assert.Equal(HttpStatusCode.Forbidden, (await otherDeveloper.PostAsJsonAsync($"/v1/dev-sessions/{session}/local-grants", grant)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/v1/dev-sessions/{session}/local-grants", grant)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/local-grants", grant)).StatusCode);
        using var socket = await DeviceSocket.ConnectAsync(factory, robot, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket);
        var accepted = await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/local-grants", grant);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var response = await accepted.Content.ReadAsStringAsync();
        ContractSchema.AssertValid("dev-sessions/v1/grant-accepted.schema.json", response);
        var json = JsonDocument.Parse(response).RootElement;
        Assert.Equal(certificate, json.GetProperty("certificate_sha256").GetString());
        Assert.Equal("https://192.168.1.10:8084", json.GetProperty("daemon_url").GetString());
        Assert.DoesNotContain(grant.token_sha256, response);
        var replay = await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/local-grants", grant);
        Assert.Equal(response, await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/dev-sessions/{session}/local-grants", new { grant_id = grantId, token_sha256 = new string('c', 64) })).StatusCode);
        using var command = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("dev.session.grant", command.Payload().GetProperty("kind").GetString());
        var payload = command.Payload().GetProperty("payload");
        ContractSchema.AssertValid("dev-sessions/v1/device-grant.schema.json", payload.GetRawText());
        Assert.Equal(grantId, payload.GetProperty("grant_id").GetGuid());
        Assert.Equal(grant.token_sha256, payload.GetProperty("token_sha256").GetString());
        var operationId = json.GetProperty("operation_id").GetGuid();
        await DeviceSocket.SendAsync(socket, robot, 2, "ack", new JsonObject { ["operation_id"] = operationId.ToString() });
        await DeviceSocket.SendAsync(socket, robot, 3, "operation_status", new JsonObject { ["operation_id"] = operationId.ToString(), ["state"] = "succeeded", ["result"] = new JsonObject { ["grant_id"] = grantId.ToString() } });
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var operationStatus = JsonDocument.Parse(await owner.GetStringAsync($"/v1/robots/{robot}/operations/{operationId}")).RootElement;
            if (operationStatus.GetProperty("state").GetString() == "succeeded") break;
            await Task.Delay(50);
        }
        var sync = new { operation_id = Guid.NewGuid(), grant_id = grantId, base_generation = 0, generation = 1,
            archive_sha256 = new string('d', 64), archive_size = 123, lock_sha256 = new string('e', 64) };
        var reportUrl = $"/v1/device/dev-sessions/{session}/generations";
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(reportUrl, sync)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync(reportUrl, sync)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync(reportUrl, sync)).StatusCode);
        var altered = new { sync.operation_id, sync.grant_id, sync.base_generation, sync.generation, sync.archive_sha256, archive_size = 124, sync.lock_sha256 };
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(reportUrl, altered)).StatusCode);
        var stale = new { operation_id = Guid.NewGuid(), sync.grant_id, sync.base_generation, sync.generation, sync.archive_sha256, sync.archive_size, sync.lock_sha256 };
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(reportUrl, stale)).StatusCode);
        var sessionState = JsonDocument.Parse(await owner.GetStringAsync($"/v1/dev-sessions/{session}")).RootElement;
        Assert.Equal(1, sessionState.GetProperty("generation").GetInt32());
        Assert.Equal(sync.lock_sha256, sessionState.GetProperty("dependency_digest").GetString());
        await RobotTests.AssertError(await device.PutAsJsonAsync("/v1/device/local-dev-endpoint", new { certificate_sha256 = "invalid", daemon_url = (string?)null }), HttpStatusCode.BadRequest, "invalid_input");
    }
}
