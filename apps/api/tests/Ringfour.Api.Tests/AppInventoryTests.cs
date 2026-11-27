using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Apps;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AppInventoryTests(PostgresFixture postgres)
{
    private static JsonObject Fixture(string name)
    {
        var values = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/fixtures/device/apps/v1/positive.json")))!.AsArray();
        return values.Single(x => x!["name"]!.GetValue<string>() == name)!["value"]!.DeepClone().AsObject();
    }

    private static HttpClient Machine(ApiFactory factory, Guid credential, string secret, Guid session)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-ringfour-credential-id", credential.ToString());
        client.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
        client.DefaultRequestHeaders.Add("x-ringfour-device-session", session.ToString());
        return client;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string route, JsonObject body, bool post = false) =>
        client.SendAsync(new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Put, route) {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });

    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        ContractSchema.AssertValid("errors/v2/app-error.schema.json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Negotiated_inventory_is_scoped_replayed_fenced_and_retained_offline()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        using var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "Inventory owner");
        var robot = await RobotTests.CreateRobot(owner, org, "inventory robot");
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        using var socket = await DeviceSocket.ConnectAsync(factory, robot, credential, secret);
        using var welcome = await DeviceSocket.ReceiveAsync(socket);
        var session = welcome.Payload().GetProperty("session_id").GetGuid();
        using var device = Machine(factory, credential, secret, session);
        var ns = Guid.NewGuid();
        var offer = new JsonObject { ["session_id"] = session.ToString(), ["deployment_namespace"] = ns.ToString(),
            ["supported_app_protocol_versions"] = new JsonArray(1), ["supports_manifest_v2"] = true };
        using var invalidCredential = Machine(factory, credential, "wrong-secret", session);
        await AssertError(await Send(invalidCredential, "/v1/device/app-protocol/negotiate", offer, true), HttpStatusCode.Unauthorized);
        await AssertError(await device.PutAsync("/v1/device/app-inventory",
            new StringContent(new string(' ', 65537), Encoding.UTF8, "application/json")), HttpStatusCode.RequestEntityTooLarge);
        var report = Fixture("base101_inventory");
        report["deployment_namespace"] = ns.ToString(); report["report_id"] = Guid.NewGuid().ToString();
        await AssertError(await Send(device, "/v1/device/app-inventory", report), HttpStatusCode.Conflict);
        var chosen = await Json(await Send(device, "/v1/device/app-protocol/negotiate", offer, true));
        Assert.Equal(chosen.ToJsonString(), (await Json(await Send(device, "/v1/device/app-protocol/negotiate", offer, true))).ToJsonString());
        var accepted = await Json(await Send(device, "/v1/device/app-inventory", report));
        var replay = (JsonObject)report.DeepClone(); replay["report_sequence"] = JsonNode.Parse("1.0");
        Assert.True(JsonNode.DeepEquals(accepted, await Json(await Send(device, "/v1/device/app-inventory", replay))));
        var conflict = (JsonObject)report.DeepClone(); conflict["platform"]!["architecture"] = "arm64";
        await AssertError(await Send(device, "/v1/device/app-inventory", conflict), HttpStatusCode.Conflict);
        var gap = (JsonObject)report.DeepClone(); gap["report_id"] = Guid.NewGuid().ToString(); gap["report_sequence"] = 3;
        await AssertError(await Send(device, "/v1/device/app-inventory", gap), HttpStatusCode.Conflict);
        var status = await Json(await owner.GetAsync($"/v1/robots/{robot}/app-status"));
        Assert.Equal("supported", status["support"]!.GetValue<string>());
        Assert.True(status["supports_manifest_v2"]!.GetValue<bool>());
        Assert.False(status["supports_live_settings"]!.GetValue<bool>());
        Assert.Equal(accepted["received_at"]!.GetValue<string>(), status["received_at"]!.GetValue<string>());
        AppProtocol.Validate(status.ToJsonString(), "robot_app_status");
        await AssertError(await outsider.GetAsync($"/v1/robots/{robot}/app-status"), HttpStatusCode.NotFound);
        using var anonymous = factory.CreateClient();
        await AssertError(await anonymous.GetAsync($"/v1/robots/{robot}/app-status"), HttpStatusCode.Unauthorized);

        using var replacement = await DeviceSocket.ConnectAsync(factory, robot, credential, secret);
        using var welcome2 = await DeviceSocket.ReceiveAsync(replacement);
        var session2 = welcome2.Payload().GetProperty("session_id").GetGuid();
        await AssertError(await Send(device, "/v1/device/app-inventory", report), HttpStatusCode.Conflict);
        using var device2 = Machine(factory, credential, secret, session2);
        offer["session_id"] = session2.ToString();
        await Json(await Send(device2, "/v1/device/app-protocol/negotiate", offer, true));
        Assert.True(JsonNode.DeepEquals(accepted, await Json(await Send(device2, "/v1/device/app-inventory", report))));
        offer["deployment_namespace"] = Guid.NewGuid().ToString();
        await AssertError(await Send(device2, "/v1/device/app-protocol/negotiate", offer, true), HttpStatusCode.Conflict);
        replacement.Abort();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            status = await Json(await owner.GetAsync($"/v1/robots/{robot}/app-status"));
            if (status["connectivity"]!.GetValue<string>() == "offline") break;
            await Task.Delay(20);
        }
        Assert.Equal("offline", status["connectivity"]!.GetValue<string>());
        Assert.Equal(accepted["received_at"]!.GetValue<string>(), status["received_at"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(report["inventory"], status["inventory"]));
    }

    [Fact]
    public void Production_reader_rejects_duplicate_fields_and_invalid_arm_instances()
    {
        var report = Fixture("base101_inventory");
        AppProtocol.Validate(report.ToJsonString(), "inventory_report");
        var invalid = report.ToJsonString().Replace("\"app_protocol_version\":1", "\"app_protocol_version\":1,\"app_protocol_version\":1");
        Assert.Throws<Ringfour.Api.Errors.ApiException>(() => AppProtocol.Validate(invalid, "inventory_report"));
        report["inventory"]!["current"]!["instances"]!["arms"] = new JsonArray("left");
        Assert.Throws<Ringfour.Api.Errors.ApiException>(() => AppProtocol.Validate(report.ToJsonString(), "inventory_report"));
    }

    [Theory]
    [InlineData("positive.json", true)]
    [InlineData("rejected.json", false)]
    public void Production_validator_matches_existing_cross_language_protocol_fixtures(string file, bool valid)
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/fixtures/device/apps/v1", file)))!.AsArray();
        var supported = new[] { "negotiation_request", "inventory_report", "robot_app_status", "compatibility" };
        foreach (var fixture in fixtures)
        {
            var reference = fixture!["schema"]!.GetValue<string>().Split("#/$defs/");
            if (reference.Length != 2) continue;
            var definition = reference[1];
            if (!supported.Contains(definition)) continue;
            if (valid) AppProtocol.Validate(fixture["value"]!.ToJsonString(), definition);
            else Assert.Throws<Ringfour.Api.Errors.ApiException>(() => AppProtocol.Validate(fixture["value"]!.ToJsonString(), definition));
        }
    }

    [Fact]
    public async Task Preview_authorizes_shared_revision_and_keeps_import_provenance_after_revocation()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var consumer = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        using var publisher = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        using var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var consumerOrg = await OrganizationTests.CreateOrganization(consumer, "Consumer");
        var publisherOrg = await OrganizationTests.CreateOrganization(publisher, "Publisher");
        var consumerId = await RobotTests.UserId(consumer);
        var publisherId = await RobotTests.UserId(publisher);
        var robot = await RobotTests.CreateRobot(consumer, consumerOrg, "Consumer robot");
        var app = Guid.NewGuid(); var revision = Guid.NewGuid(); var grant = Guid.NewGuid();
        var manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/fixtures/apps/v1/manifest.json"));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            db.Apps.Add(new AppRecord { Id = app, PublisherOrganizationId = publisherOrg, AppKey = "robot-check", Name = "Check", Description = "Test", CreatedByUserId = publisherId, CreatedAt = DateTimeOffset.UtcNow });
            db.AppRevisions.Add(new AppRevision { Id = revision, AppId = app, PublisherOrganizationId = publisherOrg, Release = "0.1.0", Manifest = manifest,
                ManifestDigest = "sha256:" + new string('a', 64), Visibility = "unlisted", PublishedByUserId = publisherId, PublishedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var route = $"/v1/robots/{robot}/app-compatibility?revision_id={revision}";
        Assert.Equal(HttpStatusCode.NotFound, (await consumer.GetAsync(route)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            db.AppShares.Add(new AppShare { Id = grant, AppId = app, RevisionId = revision, PublisherOrganizationId = publisherOrg,
                DestinationOrganizationId = consumerOrg, GrantedByUserId = publisherId, GrantedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var preview = await Json(await consumer.GetAsync(route));
        Assert.Equal("inventory_unavailable", preview["status"]!.GetValue<string>());
        AppProtocol.Validate(preview.ToJsonString(), "compatibility");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await publisher.GetAsync(route)).StatusCode); // A publisher grant does not expose consumer robots.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            db.AppImports.Add(new AppImport { Id = Guid.NewGuid(), OrganizationId = consumerOrg, AppId = app, RevisionId = revision,
                PublisherOrganizationId = publisherOrg, SourceKind = "direct_share", ShareId = grant, ImportedByUserId = consumerId, ImportedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await postgres.ExecuteAsync("UPDATE app_shares SET revoked_at = now() WHERE id = @id", ("id", grant));
        Assert.Equal(HttpStatusCode.OK, (await consumer.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData("fresh", "compatible")]
    [InlineData("optional", "compatible")]
    [InlineData("required", "missing_required")]
    [InlineData("arm", "missing_required")]
    [InlineData("stale", "inventory_unavailable")]
    [InlineData("unavailable", "inventory_unavailable")]
    [InlineData("target", "no_matching_image")]
    [InlineData("native", "native_requirement_unavailable")]
    [InlineData("agent_version", "native_requirement_unavailable")]
    [InlineData("manifest_v2", "daemon_upgrade_required")]
    public void Preview_is_fail_closed_and_matches_capability_semantics(string scenario, string expected)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/fixtures/apps/v1/manifest.json")))!.AsObject();
        var projection = Fixture("online_status");
        var now = DateTimeOffset.Parse(projection["received_at"]!.GetValue<string>());
        var capabilities = projection["inventory"]!["current"]!["capabilities"]!.AsArray();
        if (scenario == "optional") capabilities.RemoveAt(1);
        if (scenario == "required") capabilities.RemoveAt(3);
        if (scenario == "arm") manifest["capabilities"]!["required"] = new JsonArray(new JsonObject { ["capability"] = "groups", ["instance_kind"] = "arm" });
        if (scenario == "stale") now = now.AddSeconds(46);
        if (scenario == "unavailable") projection["inventory"] = new JsonObject { ["state"] = "unavailable" };
        if (scenario == "target") projection["platform"]!["architecture"] = "arm64";
        if (scenario == "native") manifest["targets"]![0]!["native_requirements"] = new JsonArray("cuda:12");
        if (scenario == "agent_version") manifest["toolchain"] = new JsonObject { ["protocol"] = 1,
            ["agent"] = new JsonObject { ["min_version"] = "1.0.0" } };
        if (scenario == "manifest_v2") { manifest["schema_version"] = 2; projection["supports_manifest_v2"] = false; }
        var result = AppCompatibility.Evaluate(manifest, projection, now);
        Assert.Equal(expected, result["status"]!.GetValue<string>());
        AppProtocol.Validate(result.ToJsonString(), "compatibility");
    }
}
