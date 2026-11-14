using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MissionDeploymentTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CandidateActivatesOnlyAfterPrepareAndFailureKeepsPreviousRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-deploy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "deploy org");
            var robot = await RobotTests.CreateRobot(owner, org, "robot");
            var (_, token) = await RobotTests.IssueToken(owner, robot);
            var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
            var (archive, digest, lockHash) = ScriptRevisionTests.CreateArchive();
            var source = new
            {
                organization_id = org,
                archive_sha256 = ScriptRevisionTests.Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "source");
            var uploaded = await owner.PostAsJsonAsync("/v1/script-revisions", source, RingfourJson.Options);
            var sourceId = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            await owner.PutAsync($"/v1/script-revisions/{sourceId}/content", new ByteArrayContent(archive));
            await owner.PostAsync($"/v1/script-revisions/{sourceId}/finalize", null);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "mission");
            var missionResponse = await owner.PostAsJsonAsync("/v1/missions", new { organization_id = org, name = "hello" }, RingfourJson.Options);
            var missionId = JsonDocument.Parse(await missionResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            var manifest = new
            {
                schema_version = 1,
                name = "hello",
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_file = "requirements.lock",
                source_digest = digest
            };
            async Task<Guid> Publish(int version)
            {
                owner.DefaultRequestHeaders.Remove("Idempotency-Key");
                owner.DefaultRequestHeaders.Add("Idempotency-Key", $"revision-{version}");
                var result = await owner.PostAsJsonAsync($"/v1/missions/{missionId}/revisions",
                    new { script_revision_id = sourceId, expected_version = version - 1, manifest }, RingfourJson.Options);
                Assert.Equal(HttpStatusCode.Created, result.StatusCode);
                return JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            }
            var firstRevision = await Publish(1);
            var secondRevision = await Publish(2);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "deploy-first");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync($"/v1/robots/{robot}/deployments", new { mission_revision_id = firstRevision }, RingfourJson.Options)).StatusCode);
            using var socket = await DeviceSocket.ConnectAsync(factory, robot, credentialId, secret);
            await DeviceSocket.ReceiveAsync(socket);
            var accepted = await owner.PostAsJsonAsync($"/v1/robots/{robot}/deployments", new { mission_revision_id = firstRevision }, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var acceptedBody = await accepted.Content.ReadAsStringAsync();
            ContractSchema.AssertValid("missions/v1/deployment-accepted.schema.json", acceptedBody);
            var document = JsonDocument.Parse(acceptedBody).RootElement;
            var deploymentId = document.GetProperty("deployment_id").GetGuid();
            var operationId = document.GetProperty("operation_id").GetGuid();
            var deploymentPage = await owner.GetStringAsync($"/v1/organizations/{org}/deployments?limit=1");
            ContractSchema.AssertValid("missions/v1/deployment-page.schema.json", deploymentPage);
            Assert.Equal(deploymentId, JsonDocument.Parse(deploymentPage).RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/organizations/{org}/deployments")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/v1/organizations/{org}/deployments?cursor=invalid")).StatusCode);
            using var command = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal("mission.prepare", command.Payload().GetProperty("kind").GetString());
            var pending = JsonDocument.Parse(await owner.GetStringAsync($"/v1/deployments/{deploymentId}")).RootElement;
            ContractSchema.AssertValid("missions/v1/deployment.schema.json", pending.GetRawText());
            Assert.Equal(JsonValueKind.Null, pending.GetProperty("selected_revision_id").ValueKind);
            Assert.Equal(firstRevision, pending.GetProperty("candidate_revision_id").GetGuid());
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/deployments/{deploymentId}")).StatusCode);
            var device = factory.CreateClient();
            device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
            device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
            Assert.Equal(archive, await device.GetByteArrayAsync($"/v1/device/deployments/{deploymentId}/candidate?operation_id={operationId}"));
            await DeviceSocket.SendAsync(socket, robot, 2, "ack", new JsonObject { ["operation_id"] = operationId.ToString() });
            await DeviceSocket.SendAsync(socket, robot, 3, "operation_status", new JsonObject
            {
                ["operation_id"] = operationId.ToString(),
                ["state"] = "succeeded",
                ["result"] = new JsonObject { ["revision_digest"] = digest, ["state"] = "ready" }
            });
            for (var attempt = 0; attempt < 60; attempt++)
            {
                pending = JsonDocument.Parse(await owner.GetStringAsync($"/v1/deployments/{deploymentId}")).RootElement;
                if (pending.GetProperty("candidate_revision_id").ValueKind == JsonValueKind.Null) break;
                await Task.Delay(50);
            }
            Assert.Equal(firstRevision, pending.GetProperty("selected_revision_id").GetGuid());
            Assert.Equal("ready", pending.GetProperty("observed_state").GetString());
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "deploy-second");
            var next = await owner.PostAsJsonAsync($"/v1/robots/{robot}/deployments", new { mission_revision_id = secondRevision }, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
            var nextOp = JsonDocument.Parse(await next.Content.ReadAsStringAsync()).RootElement.GetProperty("operation_id").GetGuid();
            using var nextCommand = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal(nextOp, nextCommand.Payload().GetProperty("operation_id").GetGuid());
            pending = JsonDocument.Parse(await owner.GetStringAsync($"/v1/deployments/{deploymentId}")).RootElement;
            Assert.Equal(firstRevision, pending.GetProperty("selected_revision_id").GetGuid());
            Assert.Equal(secondRevision, pending.GetProperty("candidate_revision_id").GetGuid());
            await DeviceSocket.SendAsync(socket, robot, 4, "ack", new JsonObject { ["operation_id"] = nextOp.ToString() });
            await DeviceSocket.SendAsync(socket, robot, 5, "operation_status", new JsonObject
            {
                ["operation_id"] = nextOp.ToString(),
                ["state"] = "failed",
                ["result"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "preparation_failed", ["message"] = "private detail" } }
            });
            for (var attempt = 0; attempt < 60; attempt++)
            {
                pending = JsonDocument.Parse(await owner.GetStringAsync($"/v1/deployments/{deploymentId}")).RootElement;
                if (pending.GetProperty("candidate_revision_id").ValueKind == JsonValueKind.Null) break;
                await Task.Delay(50);
            }
            Assert.Equal(firstRevision, pending.GetProperty("selected_revision_id").GetGuid());
            Assert.Equal("preparation_failed", pending.GetProperty("last_error").GetProperty("code").GetString());
            Assert.DoesNotContain("private detail", pending.GetRawText());

            Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/v1/deployments/{deploymentId}/start", null)).StatusCode);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "start-once");
            var started = await owner.PostAsync($"/v1/deployments/{deploymentId}/start", null);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var startBody = await started.Content.ReadAsStringAsync();
            ContractSchema.AssertValid("missions/v1/action-accepted.schema.json", startBody);
            var startJson = JsonDocument.Parse(startBody).RootElement;
            var execution = startJson.GetProperty("execution_id").GetGuid();
            var startAction = startJson.GetProperty("operation_id").GetGuid();
            var replay = await owner.PostAsync($"/v1/deployments/{deploymentId}/start", null);
            Assert.Equal(startBody, await replay.Content.ReadAsStringAsync());
            using var startCommand = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal(execution, startCommand.Payload().GetProperty("operation_id").GetGuid());
            Assert.Equal("script.execution.start", startCommand.Payload().GetProperty("kind").GetString());
            Assert.Equal(firstRevision, startCommand.Payload().GetProperty("payload").GetProperty("mission_revision_id").GetGuid());
            await DeviceSocket.SendAsync(socket, robot, 6, "ack", new JsonObject { ["operation_id"] = execution.ToString() });
            await DeviceSocket.SendAsync(socket, robot, 7, "operation_status", new JsonObject
            {
                ["operation_id"] = execution.ToString(),
                ["state"] = "succeeded",
                ["result"] = new JsonObject()
            });
            var running = new { state = "running", exit_code = (int?)null, error = (object?)null, stdout_truncated = false, stderr_truncated = false };
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync($"/v1/device/executions/{execution}/status", running, RingfourJson.Options)).StatusCode);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var action = JsonDocument.Parse(await owner.GetStringAsync($"/v1/robots/{robot}/operations/{startAction}")).RootElement;
                if (action.GetProperty("state").GetString() == "succeeded") break;
                await Task.Delay(50);
            }
            var executionJson = JsonDocument.Parse(await owner.GetStringAsync($"/v1/executions/{execution}")).RootElement;
            Assert.Equal(deploymentId, executionJson.GetProperty("deployment_id").GetGuid());
            Assert.Equal(firstRevision, executionJson.GetProperty("mission_revision_id").GetGuid());
            var history = await owner.GetStringAsync($"/v1/robots/{robot}/executions?limit=1");
            ContractSchema.AssertValid("scripts/v1/execution-page.schema.json", history);
            Assert.Equal(execution, JsonDocument.Parse(history).RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/robots/{robot}/executions")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/v1/robots/{robot}/executions?cursor=invalid")).StatusCode);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "restart-once");
            var restarted = await owner.PostAsync($"/v1/deployments/{deploymentId}/restart", null);
            Assert.Equal(HttpStatusCode.Accepted, restarted.StatusCode);
            var restartBody = await restarted.Content.ReadAsStringAsync();
            var nextExecution = JsonDocument.Parse(restartBody).RootElement.GetProperty("execution_id").GetGuid();
            Assert.NotEqual(execution, nextExecution);
            var restartReplay = await owner.PostAsync($"/v1/deployments/{deploymentId}/restart", null);
            Assert.Equal(restartBody, await restartReplay.Content.ReadAsStringAsync());
            using var stopCommand = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal("script.execution.stop", stopCommand.Payload().GetProperty("kind").GetString());
            var stopId = stopCommand.Payload().GetProperty("operation_id").GetGuid();
            await DeviceSocket.SendAsync(socket, robot, 8, "ack", new JsonObject { ["operation_id"] = stopId.ToString() });
            await DeviceSocket.SendAsync(socket, robot, 9, "operation_status", new JsonObject
            {
                ["operation_id"] = stopId.ToString(),
                ["state"] = "succeeded",
                ["result"] = new JsonObject()
            });
            var cancelled = new { state = "cancelled", exit_code = (int?)null, error = (object?)null, stdout_truncated = false, stderr_truncated = false };
            Assert.Equal(HttpStatusCode.NoContent, (await device.PutAsJsonAsync($"/v1/device/executions/{execution}/status", cancelled, RingfourJson.Options)).StatusCode);
            using var restartCommand = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal(nextExecution, restartCommand.Payload().GetProperty("operation_id").GetGuid());
            Assert.Equal("script.execution.start", restartCommand.Payload().GetProperty("kind").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
