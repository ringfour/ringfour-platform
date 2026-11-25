using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

public sealed partial class AppInstallationTests
{
    [Fact]
    public async Task RequestAndAppliedResponseAppearWithTheirActualActors()
    {
        await using var s = await Setup();
        var factory = s.Factory; var owner = s.Owner; var device = s.Device;
        var organization = s.Organization; var robot = s.Robot;
        using var operatorClient = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid():N}");
        var ownerId = await RobotTests.UserId(owner);
        var operatorId = await RobotTests.UserId(operatorClient);
        var member = await owner.PostAsJsonAsync($"/v1/organizations/{organization}/members", new { user_id = operatorId, role = "operator" }, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.Created, member.StatusCode);
        var install = await Json(await Post(owner, $"/v1/robots/{robot}/app-installations", s.Input));
        await Json(await device.PutAsJsonAsync("/v1/device/app-observations", Applied(s, await Command(s, install))));
        var path = $"/v1/app-installations/{install["installation_id"]}";
        var binding = (await Json(await owner.GetAsync(path)))["installed"]!;
        var started = await Json(await Post(owner, path + "/start", StartInput(binding)));
        var execution = Guid.Parse(started["run_id"]!.GetValue<string>());
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
            $"/v1/organizations/{organization}/app-logbook?run_id={execution}");
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
            $"/v1/organizations/{organization}/app-logbook?run_id={execution}");
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
            $"/v1/organizations/{organization}/app-logbook?run_id={execution}");
        var timedOut = finalHistory!["items"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "human.interaction.timed_out")!;
        Assert.Null(timedOut["actor_user_id"]);
    }
}
