using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScriptRunTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ExecutionOutputAndArtifactsAreScopedAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-run-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "run org");
            var robot = await RobotTests.CreateRobot(owner, org, "robot");
            var (_, token) = await RobotTests.IssueToken(owner, robot);
            var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
            var (archive, digest, lockHash) = ScriptRevisionTests.CreateArchive();
            var create = new
            {
                organization_id = org,
                archive_sha256 = ScriptRevisionTests.Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "revision-key");
            var response = await owner.PostAsJsonAsync("/v1/script-revisions", create, RingfourJson.Options);
            var revision = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync($"/v1/script-revisions/{revision}/content", new ByteArrayContent(archive))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/v1/script-revisions/{revision}/finalize", null)).StatusCode);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "run-key");
            var request = new { revision_id = revision };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/script-runs", request, RingfourJson.Options)).StatusCode);
            using var socket = await DeviceSocket.ConnectAsync(factory, robot, credentialId, secret);
            await DeviceSocket.ReceiveAsync(socket);
            await postgres.ExecuteAsync("UPDATE robot_presence SET last_seen_at=now()-interval '2 minutes' WHERE robot_id=@id", ("@id", robot));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/script-runs", request, RingfourJson.Options)).StatusCode);
            await postgres.ExecuteAsync("UPDATE robot_presence SET last_seen_at=now() WHERE robot_id=@id", ("@id", robot));
            var accepted = await owner.PostAsJsonAsync($"/v1/robots/{robot}/script-runs", request, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var acceptedBody = await accepted.Content.ReadAsStringAsync();
            ContractSchema.AssertValid("scripts/v1/create-run-response.schema.json", acceptedBody);
            var id = JsonDocument.Parse(acceptedBody).RootElement.GetProperty("execution_id").GetGuid();
            using var command = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal("script.execution.start", command.Payload().GetProperty("kind").GetString());
            await DeviceSocket.SendAsync(socket, robot, 2, "ack", new JsonObject { ["operation_id"] = id.ToString() });
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var rows = await postgres.QueryAsync("SELECT state FROM operations WHERE id=@id", ("@id", id));
                if ((string)rows[0][0]! == "Accepted") break;
                await Task.Delay(50);
            }
            await postgres.ExecuteAsync("UPDATE operations SET deadline_at=now()-interval '1 minute' WHERE id=@id", ("@id", id));
            using (var scope = factory.Services.CreateScope())
            {
                await OperationSweeper.SweepOnceAsync(scope.ServiceProvider.GetRequiredService<RingfourDbContext>(),
                    factory.Services.GetRequiredService<DeviceConnectionManager>(), DateTimeOffset.UtcNow, CancellationToken.None);
            }
            Assert.Equal("Accepted", (string)(await postgres.QueryAsync("SELECT state FROM operations WHERE id=@id", ("@id", id)))[0][0]!);
            Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/script-runs", request, RingfourJson.Options)).StatusCode);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "other-run-key");
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/script-runs", request, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/executions/{id}")).StatusCode);
            var device = factory.CreateClient();
            device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
            device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
            Assert.Equal(archive, await device.GetByteArrayAsync($"/v1/device/executions/{id}/revision"));
            var outputUrl = $"/v1/device/executions/{id}/output/stdout?offset=0";
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsync(outputUrl, new ByteArrayContent("hello"u8.ToArray()))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsync(outputUrl, new ByteArrayContent("hello"u8.ToArray()))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await device.PutAsync(outputUrl, new ByteArrayContent("other"u8.ToArray()))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await device.PutAsync($"/v1/device/executions/{id}/output/stdout?offset=10", new ByteArrayContent("x"u8.ToArray()))).StatusCode);
            var bytes = "artifact"u8.ToArray();
            var artifactUrl = $"/v1/device/executions/{id}/artifacts?path=report.txt&sha256={ScriptRevisionTests.Hash(bytes)}";
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsync(artifactUrl, new ByteArrayContent(bytes))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsync(artifactUrl, new ByteArrayContent(bytes))).StatusCode);
            var status = new { state = "succeeded", exit_code = (int?)0, error = (object?)null, stdout_truncated = false, stderr_truncated = false };
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync($"/v1/device/executions/{id}/status", status, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync($"/v1/device/executions/{id}/status", status, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await device.PutAsJsonAsync($"/v1/device/executions/{id}/status", status with { state = "running", exit_code = (int?)null }, RingfourJson.Options)).StatusCode);
            var execution = await owner.GetStringAsync($"/v1/executions/{id}");
            ContractSchema.AssertValid("scripts/v1/execution.schema.json", execution);
            Assert.Equal("succeeded", JsonDocument.Parse(execution).RootElement.GetProperty("state").GetString());
            var output = await owner.GetStringAsync($"/v1/executions/{id}/output?stream=stdout&offset=0&limit=100");
            ContractSchema.AssertValid("runner/v1/output.schema.json", output);
            Assert.Equal("hello"u8.ToArray(), Convert.FromBase64String(JsonDocument.Parse(output).RootElement.GetProperty("data_base64").GetString()!));
            Assert.Equal(bytes, await owner.GetByteArrayAsync($"/v1/executions/{id}/artifacts/content?path=report.txt"));
            var events = await postgres.QueryAsync("SELECT state FROM script_execution_events WHERE execution_id=@id ORDER BY occurred_at", ("@id", id));
            Assert.Equal(new[] { "queued", "succeeded" }, events.Select(row => (string)row[0]!).ToArray());
            var second = await owner.PostAsJsonAsync($"/v1/robots/{robot}/script-runs", request, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
            var secondId = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("execution_id").GetGuid();
            using var secondCommand = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal(secondId, secondCommand.Payload().GetProperty("operation_id").GetGuid());
            await DeviceSocket.SendAsync(socket, robot, 3, "operation_status", new JsonObject
            {
                ["operation_id"] = secondId.ToString(),
                ["state"] = "failed",
                ["result"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "execution_conflict", ["message"] = "private detail" } }
            });
            JsonElement failed = default;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                failed = JsonDocument.Parse(await owner.GetStringAsync($"/v1/executions/{secondId}")).RootElement.Clone();
                if (failed.GetProperty("state").GetString() == "failed") break;
                await Task.Delay(50);
            }
            Assert.Equal("execution_conflict", failed.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain("private detail", failed.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
