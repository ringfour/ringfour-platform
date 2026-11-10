using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RobotTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";

    internal static async Task<Guid> UserId(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/v1/me"));
        return me.RootElement.GetProperty("user").GetProperty("id").GetGuid();
    }

    internal static async Task AssertError(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        ContractSchema.AssertValid("errors/v1/error.schema.json", body);
        Assert.Equal(
            code,
            JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    internal static async Task<Guid> CreateRobot(
        HttpClient owner, Guid orgId, string name, string? profileRef = null)
    {
        var response = await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/robots",
            new { name, profile_ref = profileRef },
            RingfourJson.Options);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ContractSchema.AssertValid("robots/v1/robot.schema.json", body);
        Assert.DoesNotContain("hash", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    internal static async Task<(Guid TokenId, string Token)> IssueToken(HttpClient owner, Guid robotId)
    {
        var response = await owner.PostAsync(
            $"/v1/robots/{robotId}/enrollment-tokens", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ContractSchema.AssertValid("robots/v1/enrollment-token-response.schema.json", body);
        Assert.DoesNotContain("hash", body);
        using var document = JsonDocument.Parse(body);
        return (
            document.RootElement.GetProperty("token_id").GetGuid(),
            document.RootElement.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task Owner_creates_robot_and_audit_event_is_recorded()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var ownerId = await UserId(owner);

        var robotId = await CreateRobot(owner, orgId, "warehouse-01", "agent-repo:warehouse-01@1.2.0");

        var detail = await owner.GetStringAsync($"/v1/robots/{robotId}");
        ContractSchema.AssertValid("robots/v1/robot.schema.json", detail);
        using var document = JsonDocument.Parse(detail);
        Assert.Equal("warehouse-01", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("enrolled_at").ValueKind);
        Assert.Equal(
            "agent-repo:warehouse-01@1.2.0",
            document.RootElement.GetProperty("profile_ref").GetString());

        var list = await owner.GetStringAsync($"/v1/organizations/{orgId}/robots");
        ContractSchema.AssertValid("robots/v1/robot-list-response.schema.json", list);
        Assert.Single(JsonDocument.Parse(list).RootElement.GetProperty("items").EnumerateArray());

        var audit = await postgres.QueryAsync(
            "SELECT type, actor_user_id FROM robot_audit_events WHERE robot_id = @robotId",
            ("@robotId", robotId));
        var row = Assert.Single(audit);
        Assert.Equal("robot_created", row[0]);
        Assert.Equal(ownerId, row[1]);
    }

    [Fact]
    public async Task Robot_administration_is_owner_only_and_non_members_get_not_found()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var developer = factory.CreateAuthenticatedClient(UniqueSub());
        using var outsider = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await CreateRobot(owner, orgId, "warehouse-01");
        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = await UserId(developer), role = "developer" },
            RingfourJson.Options);

        // Developer (member, not owner) may read but not administer.
        var developerGet = await developer.GetAsync($"/v1/robots/{robotId}");
        Assert.Equal(HttpStatusCode.OK, developerGet.StatusCode);
        var developerList = await developer.GetAsync($"/v1/organizations/{orgId}/robots");
        Assert.Equal(HttpStatusCode.OK, developerList.StatusCode);
        var developerCredentials = await developer.GetAsync($"/v1/robots/{robotId}/credentials");
        Assert.Equal(HttpStatusCode.OK, developerCredentials.StatusCode);
        var developerCreate = await developer.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/robots", new { name = "nope" }, RingfourJson.Options);
        await AssertError(developerCreate, HttpStatusCode.Forbidden, "forbidden");
        var developerToken = await developer.PostAsync(
            $"/v1/robots/{robotId}/enrollment-tokens", content: null);
        await AssertError(developerToken, HttpStatusCode.Forbidden, "forbidden");
        var developerRotate = await developer.PostAsync(
            $"/v1/robots/{robotId}/credentials", content: null);
        await AssertError(developerRotate, HttpStatusCode.Forbidden, "forbidden");
        var developerRevoke = await developer.PostAsync(
            $"/v1/robots/{robotId}/credentials/{Guid.NewGuid()}/revoke", content: null);
        await AssertError(developerRevoke, HttpStatusCode.Forbidden, "forbidden");

        // Outsider (not a member) gets 404 everywhere, never 403.
        var outsiderGet = await outsider.GetAsync($"/v1/robots/{robotId}");
        await AssertError(outsiderGet, HttpStatusCode.NotFound, "not_found");
        var outsiderList = await outsider.GetAsync($"/v1/organizations/{orgId}/robots");
        await AssertError(outsiderList, HttpStatusCode.NotFound, "not_found");
        var outsiderCreate = await outsider.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/robots", new { name = "nope" }, RingfourJson.Options);
        await AssertError(outsiderCreate, HttpStatusCode.NotFound, "not_found");
        var outsiderToken = await outsider.PostAsync(
            $"/v1/robots/{robotId}/enrollment-tokens", content: null);
        await AssertError(outsiderToken, HttpStatusCode.NotFound, "not_found");
        var outsiderCredentials = await outsider.GetAsync($"/v1/robots/{robotId}/credentials");
        await AssertError(outsiderCredentials, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Rotate_requires_enrollment_and_bounds_overlap()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await CreateRobot(owner, orgId, "warehouse-01");

        var premature = await owner.PostAsync($"/v1/robots/{robotId}/credentials", content: null);
        await AssertError(premature, HttpStatusCode.BadRequest, "invalid_input");

        var (_, token) = await IssueToken(owner, robotId);
        var (credentialId, oldSecret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var ownerId = await UserId(owner);

        var before = DateTimeOffset.UtcNow;
        var rotate = await owner.PostAsync($"/v1/robots/{robotId}/credentials", content: null);
        var after = DateTimeOffset.UtcNow;
        var body = await rotate.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, rotate.StatusCode);
        ContractSchema.AssertValid("robots/v1/rotate-credential-response.schema.json", body);
        Assert.DoesNotContain("hash", body);
        using var rotated = JsonDocument.Parse(body);
        var newCredentialId = rotated.RootElement.GetProperty("credential").GetProperty("id").GetGuid();
        var newSecret = rotated.RootElement.GetProperty("secret").GetString()!;

        // The previous credential's expiry now bounds the documented overlap.
        var expiryRows = await postgres.QueryAsync(
            "SELECT expires_at FROM robot_credentials WHERE id = @id", ("@id", credentialId));
        var rawExpiry = Assert.IsType<DateTime>(Assert.Single(expiryRows)[0]);
        Assert.Equal(DateTimeKind.Utc, rawExpiry.Kind);
        var expiresAt = new DateTimeOffset(rawExpiry, TimeSpan.Zero);
        Assert.InRange(
            expiresAt,
            before.AddSeconds(299),
            after.AddSeconds(301));

        // Both secrets authenticate during the overlap window.
        await DeviceEnrollmentTests.AssertAuthenticates(factory, credentialId, oldSecret, robotId);
        await DeviceEnrollmentTests.AssertAuthenticates(factory, newCredentialId, newSecret, robotId);

        var audit = await postgres.QueryAsync(
            "SELECT type, actor_user_id FROM robot_audit_events" +
            " WHERE robot_id = @robotId AND type = 'credential_rotated'",
            ("@robotId", robotId));
        var row = Assert.Single(audit);
        Assert.Equal(ownerId, row[1]);
    }

    [Fact]
    public async Task Revoke_stops_authentication_and_is_idempotent()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await IssueToken(owner, robotId);
        var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var ownerId = await UserId(owner);

        await DeviceEnrollmentTests.AssertAuthenticates(factory, credentialId, secret, robotId);

        var revoke = await owner.PostAsync(
            $"/v1/robots/{robotId}/credentials/{credentialId}/revoke", content: null);
        var body = await revoke.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        ContractSchema.AssertValid("robots/v1/robot-credential.schema.json", body);
        Assert.Equal(
            JsonValueKind.String,
            JsonDocument.Parse(body).RootElement.GetProperty("revoked_at").ValueKind);

        var rejected = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/authenticate",
            new { credential_id = credentialId, secret },
            RingfourJson.Options);
        await AssertError(rejected, HttpStatusCode.Unauthorized, "invalid_token");

        // Second revoke is a no-op and records no extra audit event.
        var again = await owner.PostAsync(
            $"/v1/robots/{robotId}/credentials/{credentialId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var audit = await postgres.QueryAsync(
            "SELECT actor_user_id FROM robot_audit_events" +
            " WHERE robot_id = @robotId AND type = 'credential_revoked'",
            ("@robotId", robotId));
        var row = Assert.Single(audit);
        Assert.Equal(ownerId, row[0]);
    }

    [Fact]
    public async Task Robot_list_paginates_with_opaque_cursor()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var first = await CreateRobot(owner, orgId, "robot-a");
        var second = await CreateRobot(owner, orgId, "robot-b");

        var pageOneBody = await owner.GetStringAsync($"/v1/organizations/{orgId}/robots?limit=1");
        ContractSchema.AssertValid("robots/v1/robot-list-response.schema.json", pageOneBody);
        using var pageOne = JsonDocument.Parse(pageOneBody);
        Assert.Equal(first, pageOne.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        var cursor = pageOne.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var pageTwoBody = await owner.GetStringAsync(
            $"/v1/organizations/{orgId}/robots?limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        using var pageTwo = JsonDocument.Parse(pageTwoBody);
        Assert.Equal(second, pageTwo.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, pageTwo.RootElement.GetProperty("next_cursor").ValueKind);
    }
}
