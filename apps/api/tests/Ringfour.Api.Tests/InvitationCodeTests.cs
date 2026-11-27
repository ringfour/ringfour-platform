using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InvitationCodeTests(PostgresFixture postgres)
{
    private static string Sub() => "auth0|" + Guid.NewGuid();
    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }
    private static async Task<(Guid Id, string Code)> Code(HttpClient owner, Guid organization, string role = "operator")
    {
        var response = await owner.PostAsJsonAsync($"/v1/organizations/{organization}/invitation-codes", new { role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        ContractSchema.AssertValid("identity/v1/organization-invitation.schema.json", body["invitation"]!.ToJsonString());
        Assert.Null(body["invitation"]!["email"]);
        return (Guid.Parse(body["invitation"]!["id"]!.ToString()), body["code"]!.ToString());
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["code"]!.ToString());
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("developer")]
    [InlineData("owner")]
    public async Task Code_joins_a_real_account_without_SMTP_or_verified_email_and_cannot_choose_its_own_role(string role)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(Sub());
        using var recipient = factory.CreateAuthenticatedClient(Sub());
        using var other = factory.CreateAuthenticatedClient(Sub());
        var org = await OrganizationTests.CreateOrganization(owner, "Manual invitations");
        var (id, code) = await Code(owner, org, role);
        Assert.NotEqual(id.ToString(), code);
        var preview = await Json(await recipient.PostAsJsonAsync("/v1/invitations/preview", new { token = code }));
        Assert.Equal(org.ToString(), preview["organization_id"]!.ToString());
        Assert.Equal(role, preview["role"]!.ToString());
        Assert.Null(preview["email"]);
        ContractSchema.AssertValid("identity/v1/invitation-preview.schema.json", preview.ToJsonString());
        var accepted = await Json(await recipient.PostAsJsonAsync("/v1/invitations/accept", new { token = code, role = role == "operator" ? "owner" : "operator" }));
        Assert.Equal(role, accepted["role"]!.ToString());
        ContractSchema.AssertValid("identity/v1/membership.schema.json", accepted.ToJsonString());
        var replay = await Json(await recipient.PostAsJsonAsync("/v1/invitations/accept", new { token = code }));
        Assert.Equal(accepted.ToJsonString(), replay.ToJsonString());
        await Error(await other.PostAsJsonAsync("/v1/invitations/accept", new { token = code }), HttpStatusCode.Conflict, "invitation_accepted");
        var listed = await owner.GetStringAsync($"/v1/organizations/{org}/invitations");
        Assert.DoesNotContain(code, listed);
        var hashes = await postgres.QueryAsync("SELECT token_hash FROM organization_invitations WHERE id = @id", ("@id", id));
        Assert.NotEqual(code, hashes.Single()[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/v1/invitations/accept", new { token = id.ToString() })).StatusCode);
    }

    [Fact]
    public async Task Owner_can_revoke_codes_and_expired_codes_cannot_be_used()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(Sub());
        using var recipient = factory.CreateAuthenticatedClient(Sub());
        var org = await OrganizationTests.CreateOrganization(owner, "Code revocation");
        var (id, code) = await Code(owner, org);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/v1/organizations/{org}/invitations/{id}")).StatusCode);
        await Error(await recipient.PostAsJsonAsync("/v1/invitations/accept", new { token = code }), HttpStatusCode.Conflict, "invitation_revoked");
        var (expiredId, expiredCode) = await Code(owner, org);
        await postgres.ExecuteAsync("UPDATE organization_invitations SET expires_at = now() - interval '1 day' WHERE id = @id", ("@id", expiredId));
        await Error(await recipient.PostAsJsonAsync("/v1/invitations/accept", new { token = expiredCode }), HttpStatusCode.Conflict, "invitation_expired");
        await Error(await owner.PostAsJsonAsync($"/v1/organizations/{org}/invitations/{expiredId}/resend", new { }), HttpStatusCode.BadRequest, "invalid_input");
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("developer")]
    public async Task Only_owners_create_codes_and_existing_members_cannot_upgrade_with_a_code(string role)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(Sub());
        using var recipient = factory.CreateAuthenticatedClient(Sub());
        var org = await OrganizationTests.CreateOrganization(owner, "No invite escalation");
        var identity = await Json(await recipient.GetAsync("/v1/me"));
        await owner.PostAsJsonAsync($"/v1/organizations/{org}/members", new { user_id = identity["user"]!["id"]!.ToString(), role });
        await Error(await recipient.PostAsJsonAsync($"/v1/organizations/{org}/invitation-codes", new { role = "owner" }), HttpStatusCode.Forbidden, "forbidden");
        var (_, code) = await Code(owner, org, "owner");
        await Error(await recipient.PostAsJsonAsync("/v1/invitations/accept", new { token = code }), HttpStatusCode.Conflict, "already_a_member");
    }
    [Fact]
    public async Task Concurrent_accounts_cannot_consume_one_code_twice()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(Sub());
        using var first = factory.CreateAuthenticatedClient(Sub());
        using var second = factory.CreateAuthenticatedClient(Sub());
        var org = await OrganizationTests.CreateOrganization(owner, "One code one member");
        var (_, code) = await Code(owner, org);
        var results = await Task.WhenAll(first.PostAsJsonAsync("/v1/invitations/accept", new { token = code }), second.PostAsJsonAsync("/v1/invitations/accept", new { token = code }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

}
