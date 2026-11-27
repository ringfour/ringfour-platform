using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OrganizationLeaveTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";
    private static async Task<Guid> UserId(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/v1/me"));
        return me.RootElement.GetProperty("user").GetProperty("id").GetGuid();
    }
    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        ContractSchema.AssertValid("errors/v1/error.schema.json", body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("developer")]
    [InlineData("owner")]
    public async Task Any_member_can_leave_their_own_membership_and_loses_only_that_organization(string role)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var member = factory.CreateAuthenticatedClient(UniqueSub());
        var org = await OrganizationTests.CreateOrganization(owner, "Leave target");
        var retainedOrg = await OrganizationTests.CreateOrganization(member, "Retained workspace");
        var memberId = await UserId(member);
        var add = await owner.PostAsJsonAsync($"/v1/organizations/{org}/members", new { user_id = memberId, role }, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var robotResponse = await owner.PostAsJsonAsync($"/v1/organizations/{org}/robots", new { name = "Preserved robot" });
        Assert.Equal(HttpStatusCode.Created, robotResponse.StatusCode);
        using var robot = JsonDocument.Parse(await robotResponse.Content.ReadAsStringAsync());
        var robotId = robot.RootElement.GetProperty("id").GetGuid();

        var leave = await member.DeleteAsync($"/v1/organizations/{org}/members/me");
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        Assert.Empty(await leave.Content.ReadAsByteArrayAsync());
        await AssertError(await member.GetAsync($"/v1/organizations/{org}/robots"), HttpStatusCode.NotFound, "not_found");
        await AssertError(await member.GetAsync($"/v1/robots/{robotId}"), HttpStatusCode.NotFound, "not_found");
        await AssertError(await member.DeleteAsync($"/v1/organizations/{org}/members/me"), HttpStatusCode.NotFound, "not_found");
        Assert.Equal(memberId, await UserId(member));
        using var me = JsonDocument.Parse(await member.GetStringAsync("/v1/me"));
        var memberships = me.RootElement.GetProperty("memberships").EnumerateArray().ToList();
        Assert.DoesNotContain(memberships, item => item.GetProperty("organization_id").GetGuid() == org);
        Assert.Contains(memberships, item => item.GetProperty("organization_id").GetGuid() == retainedOrg);
        using var organizations = JsonDocument.Parse(await member.GetStringAsync("/v1/organizations"));
        Assert.DoesNotContain(organizations.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == org);
        using var members = JsonDocument.Parse(await owner.GetStringAsync($"/v1/organizations/{org}/members"));
        Assert.Single(members.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/v1/robots/{robotId}")).StatusCode);
    }

    [Fact]
    public async Task Sole_owner_must_promote_another_owner_before_leaving()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var org = await OrganizationTests.CreateOrganization(owner, "Keep owner");
        await AssertError(await owner.DeleteAsync($"/v1/organizations/{org}/members/me"), HttpStatusCode.Conflict, "last_owner");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/v1/organizations/{org}/members")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_non_members_cannot_leave_or_discover_an_organization()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var outsider = factory.CreateAuthenticatedClient(UniqueSub());
        using var anonymous = factory.CreateClient();
        var org = await OrganizationTests.CreateOrganization(owner, "Private workspace");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/v1/organizations/{org}/members/me")).StatusCode);
        await AssertError(await outsider.DeleteAsync($"/v1/organizations/{org}/members/me"), HttpStatusCode.NotFound, "not_found");
        await AssertError(await outsider.DeleteAsync($"/v1/organizations/{Guid.NewGuid()}/members/me"), HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_owner_leaves_or_demotion_keep_one_owner(bool demote)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var first = factory.CreateAuthenticatedClient(UniqueSub());
        using var second = factory.CreateAuthenticatedClient(UniqueSub());
        var org = await OrganizationTests.CreateOrganization(first, "Concurrent owner changes");
        var secondId = await UserId(second);
        Assert.Equal(HttpStatusCode.Created, (await first.PostAsJsonAsync($"/v1/organizations/{org}/members", new { user_id = secondId, role = "owner" }, RingfourJson.Options)).StatusCode);
        var results = await Task.WhenAll(
            first.DeleteAsync($"/v1/organizations/{org}/members/me"),
            demote ? second.PatchAsJsonAsync($"/v1/organizations/{org}/members/{secondId}", new { role = "developer" }, RingfourJson.Options)
                   : second.DeleteAsync($"/v1/organizations/{org}/members/me"));
        Assert.Single(results, response => response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
        await AssertError(Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "last_owner");
        var rows = await postgres.QueryAsync("SELECT count(*) FROM memberships WHERE organization_id = @org AND role = 'Owner'", ("org", org));
        Assert.Equal(1L, rows.Single()[0]);
    }
}
