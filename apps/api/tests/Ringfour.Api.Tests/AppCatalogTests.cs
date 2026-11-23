using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Ringfour.Api.Apps;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AppCatalogTests(PostgresFixture postgres)
{
    private static JsonObject Manifest(string image)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/fixtures/apps/v1/manifest.json")))!.AsObject();
        manifest["app_key"] = "check-" + Guid.NewGuid().ToString("N"); manifest["targets"]![0]!["image"] = image;
        return manifest;
    }
    private static Task<HttpResponseMessage> Post(HttpClient client, string route, JsonNode? body, string? key = null) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, route) {
            Headers = { { "Idempotency-Key", key ?? Guid.NewGuid().ToString() } },
            Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
    private static async Task<JsonObject> Json(HttpResponseMessage response, string? definition = null)
    {
        var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        if (definition is not null)
        {
            var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
            foreach (var file in new[] { "manifest", "api" }) JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"contracts/schemas/apps/v1/{file}.schema.json")), options);
            var schema = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v1/api.schema.json#/$defs/" + definition + "\"}", options);
            using var document = JsonDocument.Parse(text); Assert.True(schema.Evaluate(document.RootElement).IsValid, text);
        }
        return JsonNode.Parse(text)!.AsObject();
    }
    private static async Task Error(HttpResponseMessage response, int status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(); Assert.Equal(status, (int)response.StatusCode);
        ContractSchema.AssertValid("errors/v2/app-error.schema.json", text);
        Assert.Equal(code, JsonNode.Parse(text)!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Production_manifest_validator_matches_semantic_fixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts/fixtures/apps/v1");
        var manifest = File.ReadAllText(Path.Combine(root, "manifest.json")); AppManifest.Validate(manifest);
        foreach (var invalid in JsonNode.Parse(File.ReadAllText(Path.Combine(root, "rejected.json")))!.AsArray())
            Assert.Throws<ApiException>(() => AppManifest.Validate(invalid!["manifest"]!.ToJsonString()));
        var canonical = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "canonical.json")))!;
        Assert.Equal(canonical["digest"]!.GetValue<string>(), "sha256:" + AppProtocol.Digest(AppManifest.Validate(manifest)));
    }

    [Fact]
    public async Task Publish_discover_import_and_retry_are_atomic_and_workspace_scoped()
    {
        await using var registry = new RegistryFixture();
        await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var a = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); using var b = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        using var c = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var orgA = await OrganizationTests.CreateOrganization(a, "Publisher"); var orgB = await OrganizationTests.CreateOrganization(b, "Consumer");
        var orgC = await OrganizationTests.CreateOrganization(c, "Other");
        var body = new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "listed" }; var key = Guid.NewGuid().ToString();
        var route = $"/v1/organizations/{orgA}/app-revisions";
        var revision = await Json(await Post(a, route, body, key), "revision"); var count = registry.Requests;
        Assert.True(count >= 2); Assert.True(JsonNode.DeepEquals(revision, await Json(await Post(a, route, body, key))));
        registry.Corrupt = true;
        Assert.True(JsonNode.DeepEquals(revision, await Json(await Post(a, route, body)))); Assert.Equal(count, registry.Requests);
        body["manifest"]!["display"]!["name"] = "Changed";
        await Error(await Post(a, route, body, key), 409, "idempotency_conflict"); await Error(await Post(a, route, body), 409, "revision_conflict");
        await Error(await Post(b, route, body), 404, "not_found");
        var page = await Json(await b.GetAsync($"/v1/store/apps?organization_id={orgB}&q={revision["manifest"]!["app_key"]}"), "app_page");
        Assert.Single(page["items"]!.AsArray());
        await Json(await b.GetAsync($"/v1/apps/{revision["app_id"]}"), "app");
        await Json(await b.GetAsync($"/v1/app-revisions/{revision["id"]}"), "revision");
        var input = new JsonObject { ["revision_id"] = revision["id"]!.DeepClone(), ["share_id"] = null };
        var imports = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Post(b, $"/v1/organizations/{orgB}/app-imports", input)));
        var values = await Task.WhenAll(imports.Select(r => Json(r, "import"))); Assert.Single(values.Select(v => v["id"]!.ToString()).Distinct());
        Assert.Equal(orgA.ToString(), values[0]["publisher_organization_id"]!.GetValue<string>());
        await Error(await c.GetAsync($"/v1/organizations/{orgB}/app-imports"), 404, "not_found");
        await Error(await a.GetAsync($"/v1/organizations/{orgB}/app-imports"), 404, "not_found");
        var userB = await Json(await b.GetAsync("/v1/me"));
        await postgres.ExecuteAsync("UPDATE memberships SET role = 'Operator' WHERE organization_id = @org", ("org", orgB));
        await Error(await Post(b, $"/v1/organizations/{orgB}/app-imports", input), 403, "forbidden");
        await Json(await b.GetAsync($"/v1/organizations/{orgB}/app-imports"), "import_page");
        await Error(await b.GetAsync($"/v1/store/apps?organization_id={orgB}&limit=101"), 400, "invalid_input");
    }

    [Fact]
    public async Task Unlisted_grants_bind_destination_expiry_and_preserve_prior_imports()
    {
        await using var registry = new RegistryFixture(); await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var a = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); using var b = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); using var c = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var orgA = await OrganizationTests.CreateOrganization(a, "A"); var orgB = await OrganizationTests.CreateOrganization(b, "B"); var orgC = await OrganizationTests.CreateOrganization(c, "C");
        var revision = await Json(await Post(a, $"/v1/organizations/{orgA}/app-revisions", new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "unlisted" }));
        var id = revision["id"]!.GetValue<string>();
        await Error(await b.GetAsync($"/v1/app-revisions/{id}"), 404, "not_found");
        var input = new JsonObject { ["revision_id"] = id, ["share_id"] = null };
        await Error(await Post(b, $"/v1/organizations/{orgB}/app-imports", input), 404, "not_found");
        var share = await Json(await Post(a, $"/v1/app-revisions/{id}/shares", new JsonObject { ["destination_organization_id"] = orgB.ToString(), ["expires_at"] = AppProtocol.Timestamp(DateTimeOffset.UtcNow.AddHours(1)) }), "share");
        await Json(await b.GetAsync($"/v1/app-revisions/{id}"));
        await Error(await Post(b, $"/v1/organizations/{orgB}/app-imports", input), 409, "share_required");
        input["share_id"] = share["id"]!.DeepClone();
        await Error(await Post(c, $"/v1/organizations/{orgC}/app-imports", input), 404, "not_found");
        var importKey = Guid.NewGuid().ToString(); var imported = await Json(await Post(b, $"/v1/organizations/{orgB}/app-imports", input, importKey), "import");
        await Json(await Post(a, $"/v1/app-shares/{share["id"]}/revoke", null), "share");
        Assert.True(JsonNode.DeepEquals(imported, await Json(await Post(b, $"/v1/organizations/{orgB}/app-imports", input, importKey))));
        Assert.True(JsonNode.DeepEquals(imported, await Json(await Post(b, $"/v1/organizations/{orgB}/app-imports", input))));
        await Json(await b.GetAsync($"/v1/app-revisions/{id}")); await Error(await c.GetAsync($"/v1/app-revisions/{id}"), 404, "not_found");
        var second = new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "unlisted" };
        revision = await Json(await Post(a, $"/v1/organizations/{orgA}/app-revisions", second)); id = revision["id"]!.GetValue<string>();
        share = await Json(await Post(a, $"/v1/app-revisions/{id}/shares", new JsonObject { ["destination_organization_id"] = orgB.ToString(), ["expires_at"] = AppProtocol.Timestamp(DateTimeOffset.UtcNow.AddHours(1)) }));
        input["revision_id"] = id; input["share_id"] = share["id"]!.DeepClone();
        await postgres.ExecuteAsync("UPDATE app_shares SET expires_at = granted_at + interval '1 microsecond' WHERE id = @id", ("id", Guid.Parse(share["id"]!.GetValue<string>())));
        await Error(await Post(b, $"/v1/organizations/{orgB}/app-imports", input), 409, "share_expired");
        await Json(await Post(a, $"/v1/app-shares/{share["id"]}/revoke", null));
        await Error(await Post(b, $"/v1/organizations/{orgB}/app-imports", input), 409, "share_revoked");
    }

    [Theory]
    [InlineData("digest")][InlineData("index")][InlineData("platform")][InlineData("redirect")][InlineData("unlisted_host")][InlineData("long_token")]
    public async Task Registry_failure_publishes_nothing_and_leaks_no_credentials(string failure)
    {
        await using var registry = new RegistryFixture(failure == "platform" ? "arm64" : "amd64") { Private = failure == "long_token", TokenSeconds = 600 };
        if (failure == "digest") registry.Corrupt = true;
        if (failure == "index") registry.MediaType = "application/vnd.oci.image.index.v1+json";
        if (failure == "redirect") registry.Redirect = true;
        await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: failure == "unlisted_host" ? null : registry.Configuration());
        using var a = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); var org = await OrganizationTests.CreateOrganization(a, "Fail closed");
        factory.Services.GetRequiredService<IConfiguration>()["Apps:Registries:0:PublisherOrganizationId"] = org.ToString();
        await Error(await Post(a, $"/v1/organizations/{org}/app-revisions", new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "listed" }), 503, "revision_unavailable");
        Assert.Equal(0L, (await postgres.QueryAsync("SELECT count(*) FROM apps WHERE publisher_organization_id = @org", ("org", org)))[0][0]);
        Assert.Equal(0L, (await postgres.QueryAsync("SELECT count(*) FROM app_idempotency_records WHERE target = @target", ("target", org.ToString())))[0][0]);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Pull_grants_are_ephemeral_negotiated_and_bound_to_robot_candidate_and_deadline(bool privateRegistry)
    {
        await using var registry = new RegistryFixture { Private = privateRegistry };
        await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var owner = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var org = await OrganizationTests.CreateOrganization(owner, "Device owner"); var user = await RobotTests.UserId(owner);
        factory.Services.GetRequiredService<IConfiguration>()["Apps:Registries:0:PublisherOrganizationId"] = org.ToString();
        var robot = await RobotTests.CreateRobot(owner, org, "Pull device");
        var revision = await Json(await Post(owner, $"/v1/organizations/{org}/app-revisions", new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "listed" }));
        var revisionId = Guid.Parse(revision["id"]!.GetValue<string>()); var appId = Guid.Parse(revision["app_id"]!.GetValue<string>());
        var imported = await Json(await Post(owner, $"/v1/organizations/{org}/app-imports", new JsonObject { ["revision_id"] = revisionId.ToString(), ["share_id"] = null }));
        var installation = Guid.NewGuid(); var operation = Guid.NewGuid(); var config = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            db.AppInstallations.Add(new AppInstallation { Id = installation, OrganizationId = org, RobotId = robot, AppId = appId,
                ImportId = Guid.Parse(imported["id"]!.GetValue<string>()), CreatedByUserId = user, CreatedAt = now,
                Candidate = new JsonObject { ["operation_id"] = operation.ToString(), ["revision_id"] = revisionId.ToString(), ["configuration_revision_id"] = config.ToString(),
                    ["image"] = registry.Image, ["state"] = "staging", ["requested_at"] = AppProtocol.Timestamp(now), ["observed_at"] = null, ["staged_at"] = null, ["failure"] = null }.ToJsonString() });
            db.AppConfigurationRevisions.Add(new AppConfigurationRevision { Id = config, InstallationId = installation, AppId = appId, RevisionId = revisionId, CreatedByUserId = user, CreatedAt = now, Values = "{\"sample_count\":1}" });
            db.AppOperations.Add(new AppOperation { Id = operation, OrganizationId = org, RobotId = robot, InstallationId = installation, Kind = "install", State = "queued",
                Phase = "queued", RequestedByUserId = user, RequestedAt = now, DeadlineAt = now.AddMinutes(10) });
            await db.SaveChangesAsync();
        }
        var (_, token) = await RobotTests.IssueToken(owner, robot); var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        using var socket = await DeviceSocket.ConnectAsync(factory, robot, credential, secret); using var welcome = await DeviceSocket.ReceiveAsync(socket);
        var session = welcome.Payload().GetProperty("session_id").GetGuid(); using var machine = factory.CreateClient();
        machine.DefaultRequestHeaders.Add("x-ringfour-credential-id", credential.ToString()); machine.DefaultRequestHeaders.Add("x-ringfour-credential-secret", secret);
        machine.DefaultRequestHeaders.Add("x-ringfour-device-session", session.ToString());
        var body = new JsonObject { ["operation_id"] = operation.ToString(), ["revision_id"] = revisionId.ToString(), ["image"] = registry.Image };
        const string route = "/v1/device/app-registry-grants";
        await Error(await machine.PostAsJsonAsync(route, body), 409, "daemon_upgrade_required");
        await Json(await machine.PostAsJsonAsync("/v1/device/app-protocol/negotiate", new JsonObject {
            ["session_id"] = session.ToString(), ["deployment_namespace"] = Guid.NewGuid().ToString(), ["supported_app_protocol_versions"] = new JsonArray(1) }));
        var response = await machine.PostAsJsonAsync(route, body); Assert.True(response.Headers.CacheControl?.NoStore);
        var grant = await Json(response, "registry_grant"); Assert.Equal(robot.ToString(), grant["robot_id"]!.GetValue<string>());
        Assert.Equal(privateRegistry ? "bearer" : "anonymous", grant["authorization"]!["kind"]!.GetValue<string>());
        Assert.True(DateTimeOffset.Parse(grant["expires_at"]!.GetValue<string>()) <= DateTimeOffset.UtcNow.AddMinutes(5));
        var refreshed = await Json(await machine.PostAsJsonAsync(route, body));
        if (privateRegistry)
        {
            Assert.NotEqual(grant["authorization"]!["token"]!.ToString(), refreshed["authorization"]!["token"]!.ToString());
            Assert.All(registry.Scopes, scope => Assert.Equal("repository:apps/check:pull", scope));
            var persisted = (string)(await postgres.QueryAsync("SELECT string_agg(outcome::text, '') FROM app_idempotency_records WHERE principal_user_id = @id", ("id", user)))[0][0]!;
            Assert.All(registry.IssuedTokens, value => Assert.DoesNotContain(value, persisted));
        }
        var before = registry.Requests; body["revision_id"] = Guid.NewGuid().ToString();
        await Error(await machine.PostAsJsonAsync(route, body), 404, "not_found"); Assert.Equal(before, registry.Requests); body["revision_id"] = revisionId.ToString();
        body["image"] = registry.Image.Replace("apps/check", "apps/other"); await Error(await machine.PostAsJsonAsync(route, body), 404, "not_found"); body["image"] = registry.Image;
        registry.BeforeConfig = () => postgres.ExecuteAsync("UPDATE robot_credentials SET revoked_at = now() WHERE id = @id", ("id", credential));
        await Error(await machine.PostAsJsonAsync(route, body), 401, "unauthenticated");
        registry.BeforeConfig = null;
        await postgres.ExecuteAsync("UPDATE robot_credentials SET revoked_at = NULL WHERE id = @id", ("id", credential));
        var otherRobot = await RobotTests.CreateRobot(owner, org, "Other pull owner"); var otherInstallation = Guid.NewGuid(); var otherOperation = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            db.AppInstallations.Add(new AppInstallation { Id = otherInstallation, OrganizationId = org, RobotId = otherRobot, AppId = appId,
                ImportId = Guid.Parse(imported["id"]!.GetValue<string>()), CreatedByUserId = user, CreatedAt = now });
            db.AppOperations.Add(new AppOperation { Id = otherOperation, OrganizationId = org, RobotId = otherRobot, InstallationId = otherInstallation,
                Kind = "install", State = "queued", Phase = "queued", RequestedByUserId = user, RequestedAt = now, DeadlineAt = now.AddMinutes(10) });
            await db.SaveChangesAsync();
        }
        body["operation_id"] = otherOperation.ToString(); before = registry.Requests;
        await Error(await machine.PostAsJsonAsync(route, body), 404, "not_found"); Assert.Equal(before, registry.Requests);
        body["operation_id"] = operation.ToString();
        await postgres.ExecuteAsync("UPDATE app_operations SET deadline_at = requested_at WHERE id = @id", ("id", operation));
        await Error(await machine.PostAsJsonAsync(route, body), 409, "deadline_exceeded");
        using var anonymous = factory.CreateClient(); await Error(await anonymous.PostAsJsonAsync(route, body), 401, "unauthenticated");
        using var replacement = await DeviceSocket.ConnectAsync(factory, robot, credential, secret); using var replacedWelcome = await DeviceSocket.ReceiveAsync(replacement);
        await Error(await machine.PostAsJsonAsync(route, body), 409, "session_replaced");
    }

    [Fact]
    public async Task Pagination_filters_revisions_and_concurrent_publication_preserves_one_identity()
    {
        await using var registry = new RegistryFixture(); await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var a = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); using var b = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var orgA = await OrganizationTests.CreateOrganization(a, "Paged publisher"); var orgB = await OrganizationTests.CreateOrganization(b, "Paged consumer");
        var marker = "find-" + Guid.NewGuid().ToString("N"); var route = $"/v1/organizations/{orgA}/app-revisions";
        var body = new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "listed" }; body["manifest"]!["display"]!["name"] = marker;
        var responses = await Task.WhenAll(Post(a, route, body), Post(a, route, body));
        var first = await Json(responses[0]); var duplicate = await Json(responses[1]); Assert.True(JsonNode.DeepEquals(first, duplicate));
        body["manifest"]!["release"] = "0.2.0"; body["visibility"] = "unlisted";
        body["manifest"]!["display"]!["name"] = "Private release display";
        var privateRevision = await Json(await Post(a, route, body));
        await Error(await b.GetAsync($"/v1/app-revisions/{privateRevision["id"]}"), 404, "not_found");
        var publicApp = await Json(await b.GetAsync($"/v1/apps/{first["app_id"]}")); Assert.Equal(marker, publicApp["name"]!.GetValue<string>());
        var visible = await Json(await b.GetAsync($"/v1/apps/{first["app_id"]}/revisions?limit=1"), "revision_page");
        Assert.Single(visible["items"]!.AsArray()); Assert.Null(visible["next_cursor"]);
        body["manifest"] = Manifest(registry.Image); body["manifest"]!["display"]!["name"] = marker; body["visibility"] = "listed";
        await Json(await Post(a, route, body));
        var page1 = await Json(await b.GetAsync($"/v1/store/apps?organization_id={orgB}&q={marker}&limit=1"), "app_page");
        var page2 = await Json(await b.GetAsync($"/v1/store/apps?organization_id={orgB}&q={marker}&limit=1&cursor={page1["next_cursor"]}"), "app_page");
        Assert.Single(page2["items"]!.AsArray()); Assert.NotEqual(page1["items"]![0]!["id"]!.ToString(), page2["items"]![0]!["id"]!.ToString());
        Assert.Null(page2["next_cursor"]);
        var ownerPage = await Json(await a.GetAsync($"/v1/apps/{first["app_id"]}/revisions?limit=1"));
        await Json(await a.GetAsync($"/v1/apps/{first["app_id"]}/revisions?limit=1&cursor={ownerPage["next_cursor"]}"), "revision_page");
        await Error(await b.GetAsync($"/v1/store/apps?organization_id={orgB}&cursor=bad"), 400, "invalid_input");
        using var anonymous = factory.CreateClient(); await Error(await anonymous.GetAsync($"/v1/apps/{first["app_id"]}"), 401, "unauthenticated");
        using var noKey = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        await Error(await a.SendAsync(noKey), 400, "invalid_input");
        var invalidJson = new HttpRequestMessage(HttpMethod.Post, route) { Headers = { { "Idempotency-Key", "duplicate-input" } },
            Content = new StringContent("{\"visibility\":\"listed\",\"visibility\":\"listed\",\"manifest\":" + body["manifest"]!.ToJsonString() + "}", Encoding.UTF8, "application/json") };
        await Error(await a.SendAsync(invalidJson), 400, "invalid_input");
    }

    [Fact]
    public async Task Valid_utf8_manifest_is_not_rejected_after_json_escape_expansion()
    {
        await using var registry = new RegistryFixture(); await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var owner = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); var org = await OrganizationTests.CreateOrganization(owner, "Unicode publisher");
        var manifest = Manifest(registry.Image); manifest["display"]!["description"] = new string('\u03bb', 4096);
        manifest["configuration"] = JsonNode.Parse("{\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"first\":{\"type\":\"string\"},\"second\":{\"type\":\"string\"}},\"required\":[\"first\",\"second\"]},\"defaults\":{}}");
        manifest["configuration"]!["defaults"]!["first"] = new string('\u03bb', 3900); manifest["configuration"]!["defaults"]!["second"] = new string('\u03bb', 3900);
        var body = new JsonObject { ["manifest"] = manifest, ["visibility"] = "listed" };
        var raw = body.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(Encoding.UTF8.GetByteCount(raw) < 65536); Assert.True(body.ToJsonString().Length > 65536);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/organizations/{org}/app-revisions") { Headers = { { "Idempotency-Key", "unicode" } },
            Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        await Json(await owner.SendAsync(request), "revision");
    }

    [Fact]
    public async Task Private_repository_cannot_be_republished_by_another_workspace_with_a_known_digest()
    {
        await using var registry = new RegistryFixture { Private = true }; await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var a = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); using var b = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var orgA = await OrganizationTests.CreateOrganization(a, "Private publisher"); var orgB = await OrganizationTests.CreateOrganization(b, "Other publisher");
        var body = new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "unlisted" };
        await Error(await Post(a, $"/v1/organizations/{orgA}/app-revisions", body), 503, "revision_unavailable"); Assert.Equal(0, registry.Requests);
        factory.Services.GetRequiredService<IConfiguration>()["Apps:Registries:0:PublisherOrganizationId"] = orgA.ToString();
        await Json(await Post(a, $"/v1/organizations/{orgA}/app-revisions", body)); var count = registry.Requests;
        await Error(await Post(b, $"/v1/organizations/{orgB}/app-revisions", body), 503, "revision_unavailable"); Assert.Equal(count, registry.Requests);
    }

    [Fact]
    public async Task Reciprocal_shares_can_be_granted_concurrently_without_workspace_fk_deadlock()
    {
        await using var registry = new RegistryFixture(); await using var factory = new ApiFactory(postgres.ConnectionString, extraConfiguration: registry.Configuration());
        using var a = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid()); using var b = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var orgA = await OrganizationTests.CreateOrganization(a, "Reciprocal A"); var orgB = await OrganizationTests.CreateOrganization(b, "Reciprocal B");
        var revisionA = await Json(await Post(a, $"/v1/organizations/{orgA}/app-revisions", new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "unlisted" }));
        var revisionB = await Json(await Post(b, $"/v1/organizations/{orgB}/app-revisions", new JsonObject { ["manifest"] = Manifest(registry.Image), ["visibility"] = "unlisted" }));
        for (var round = 0; round < 3; round++)
        {
            var results = await Task.WhenAll(Post(a, $"/v1/app-revisions/{revisionA["id"]}/shares", new JsonObject { ["destination_organization_id"] = orgB.ToString(), ["expires_at"] = AppProtocol.Timestamp(DateTimeOffset.UtcNow.AddHours(1)) }),
                Post(b, $"/v1/app-revisions/{revisionB["id"]}/shares", new JsonObject { ["destination_organization_id"] = orgA.ToString(), ["expires_at"] = AppProtocol.Timestamp(DateTimeOffset.UtcNow.AddHours(1)) }));
            foreach (var result in results) await Json(result, "share");
        }
    }
}
