using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InteractionLogbookTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RequestAndAppliedResponseAppearWithTheirActualActors()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid():N}");
        using var operatorClient = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid():N}");
        var organization = await OrganizationTests.CreateOrganization(owner, "interaction logbook");
        var robot = await RobotTests.CreateRobot(owner, organization, "interaction robot");
        var ownerId = await RobotTests.UserId(owner);
        var operatorId = await RobotTests.UserId(operatorClient);
        var member = await owner.PostAsJsonAsync($"/v1/organizations/{organization}/members",
            new { user_id = operatorId, role = "operator" }, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.Created, member.StatusCode);

        var (_, pairingToken) = await RobotTests.IssueToken(owner, robot);
        var (credentialId, credentialSecret) = await DeviceEnrollmentTests.Enroll(factory, pairingToken);
        using var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("x-ringfour-credential-id", credentialId.ToString());
        device.DefaultRequestHeaders.Add("x-ringfour-credential-secret", credentialSecret);

        var session = Guid.NewGuid();
        var execution = Guid.NewGuid();
        var operation = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO operations (id, robot_id, kind, payload_version, payload, state, created_at, updated_at, deadline_at)
            VALUES (@operation, @robot, 'fixture.test', 1, '{}', 'Queued', now(), now(), now() + interval '1 hour');
            INSERT INTO dev_sessions (id, robot_id, owner_user_id, state, generation, created_at, updated_at, expires_at)
            VALUES (@session, @robot, @owner, 'running', 1, now(), now(), now() + interval '1 hour');
            INSERT INTO dev_session_runs (id, session_id, robot_id, actor_user_id, operation_id, generation, state, created_at, updated_at)
            VALUES (@execution, @session, @robot, @owner, @operation, 1, 'running', now(), now());
            """, ("operation", operation), ("robot", robot), ("session", session),
            ("owner", ownerId), ("execution", execution));

        var requestId = Guid.NewGuid();
        var created = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2000);
        var syncBody = new
        {
            request_id = requestId, execution_id = execution, prompt = "Continue inspection?",
            choices = new[] { new { value = "continue", label = "Continue inspection" }, new { value = "stop", label = "Stop safely" } },
            deadline = (DateTimeOffset?)null, timeout_outcome = (string?)null, state = "pending",
            outcome = (string?)null, applied_response_id = (Guid?)null, created_at = created, updated_at = created
        };
        var sync = await device.PutAsJsonAsync($"/v1/device/interactions/{requestId}", syncBody, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        using var responseRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/interactions/{requestId}/responses")
        {
            Content = JsonContent.Create(new { chosen_value = "continue" }, options: RingfourJson.Options)
        };
        responseRequest.Headers.Add("Idempotency-Key", "interaction-logbook-test");
        using var response = await operatorClient.SendAsync(responseRequest);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["response_id"]!.GetValue<Guid>();
        var pendingHistory = await owner.GetFromJsonAsync<JsonObject>(
            $"/v1/organizations/{organization}/events?execution_id={execution}");
        Assert.Single(pendingHistory!["items"]!.AsArray());

        var answered = new
        {
            request_id = requestId, execution_id = execution, prompt = syncBody.prompt,
            choices = syncBody.choices, deadline = (DateTimeOffset?)null, timeout_outcome = (string?)null,
            state = "answered", outcome = "continue", applied_response_id = responseId,
            created_at = created, updated_at = DateTimeOffset.UtcNow
        };
        sync = await device.PutAsJsonAsync($"/v1/device/interactions/{requestId}", answered, RingfourJson.Options);
        Assert.True(sync.StatusCode == HttpStatusCode.OK,
            $"Expected applied interaction sync to succeed; received {(int)sync.StatusCode}: {await sync.Content.ReadAsStringAsync()}");

        var history = await owner.GetFromJsonAsync<JsonObject>(
            $"/v1/organizations/{organization}/events?execution_id={execution}");
        var events = history!["items"]!.AsArray();
        Assert.Equal(2, events.Count);
        var requested = events.Single(item => item!["type"]!.GetValue<string>() == "human.interaction.requested")!;
        var applied = events.Single(item => item!["type"]!.GetValue<string>() == "human.interaction.answered")!;
        Assert.Equal(ownerId, requested["actor_user_id"]!.GetValue<Guid>());
        Assert.Equal(operatorId, applied["actor_user_id"]!.GetValue<Guid>());
        Assert.Null(applied["local_sequence"]);
        Assert.Equal("Continue inspection", applied["payload"]!["selected_label"]!.GetValue<string>());
        Assert.Equal(responseId, applied["payload"]!["response_id"]!.GetValue<Guid>());

        var timeoutId = Guid.NewGuid();
        var timedOutAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var timeoutSync = new
        {
            request_id = timeoutId, execution_id = execution, prompt = "Timeout?",
            choices = new[] { new { value = "continue", label = "Continue" }, new { value = "cancel", label = "Cancel safely" } },
            deadline = timedOutAt.AddSeconds(-1), timeout_outcome = "cancel", state = "timed_out",
            outcome = "cancel", applied_response_id = (Guid?)null,
            created_at = timedOutAt.AddSeconds(-10), updated_at = timedOutAt
        };
        sync = await device.PutAsJsonAsync($"/v1/device/interactions/{timeoutId}", timeoutSync, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var finalHistory = await owner.GetFromJsonAsync<JsonObject>(
            $"/v1/organizations/{organization}/events?execution_id={execution}");
        var timedOut = finalHistory!["items"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "human.interaction.timed_out")!;
        Assert.Null(timedOut["actor_user_id"]);
        var actorFiltered = await owner.GetFromJsonAsync<JsonObject>(
            $"/v1/organizations/{organization}/events?execution_id={execution}&actor_user_id={ownerId}");
        Assert.DoesNotContain(actorFiltered!["items"]!.AsArray(), item =>
            item!["type"]!.GetValue<string>() == "human.interaction.timed_out");
    }
}
