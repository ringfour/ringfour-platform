using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MembershipTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";

    private static async Task<Guid> UserId(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/v1/me"));
        return me.RootElement.GetProperty("user").GetProperty("id").GetGuid();
    }

    private static async Task AssertError(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        ContractSchema.AssertValid("errors/v1/error.schema.json", body);
        Assert.Equal(
            code,
            JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Owner_adds_member_and_lists_members()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var invitee = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var inviteeId = await UserId(invitee);

        var response = await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = inviteeId, role = "developer" },
            RingfourJson.Options);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ContractSchema.AssertValid("identity/v1/membership.schema.json", body);
        Assert.Equal("developer", JsonDocument.Parse(body).RootElement.GetProperty("role").GetString());

        var list = await owner.GetStringAsync($"/v1/organizations/{orgId}/members");
        ContractSchema.AssertValid("identity/v1/member-list-response.schema.json", list);
        Assert.Equal(
            2, JsonDocument.Parse(list).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Non_owner_member_cannot_mutate_membership()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var developer = factory.CreateAuthenticatedClient(UniqueSub());
        using var third = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var developerId = await UserId(developer);
        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = developerId, role = "developer" },
            RingfourJson.Options);

        var response = await developer.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = await UserId(third), role = "operator" },
            RingfourJson.Options);
        await AssertError(response, HttpStatusCode.Forbidden, "forbidden");

        var patch = await developer.PatchAsJsonAsync(
            $"/v1/organizations/{orgId}/members/{developerId}",
            new { role = "owner" },
            RingfourJson.Options);
        await AssertError(patch, HttpStatusCode.Forbidden, "forbidden");

        var delete = await developer.DeleteAsync(
            $"/v1/organizations/{orgId}/members/{developerId}");
        await AssertError(delete, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Cross_org_access_is_not_found_not_forbidden()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var outsider = factory.CreateAuthenticatedClient(UniqueSub());
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Secret Robotics");

        var list = await outsider.GetAsync($"/v1/organizations/{orgId}/members");
        await AssertError(list, HttpStatusCode.NotFound, "not_found");

        var add = await outsider.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = await UserId(outsider), role = "operator" },
            RingfourJson.Options);
        await AssertError(add, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Duplicate_membership_conflicts_and_unknown_user_is_not_found()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var invitee = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var inviteeId = await UserId(invitee);
        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = inviteeId, role = "operator" },
            RingfourJson.Options);

        var duplicate = await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = inviteeId, role = "operator" },
            RingfourJson.Options);
        await AssertError(duplicate, HttpStatusCode.Conflict, "conflict");

        var unknown = await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = Guid.NewGuid(), role = "operator" },
            RingfourJson.Options);
        await AssertError(unknown, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Role_change_applies_and_member_list_is_visible_to_members()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var member = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var memberId = await UserId(member);
        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = memberId, role = "operator" },
            RingfourJson.Options);

        var patch = await owner.PatchAsJsonAsync(
            $"/v1/organizations/{orgId}/members/{memberId}",
            new { role = "developer" },
            RingfourJson.Options);
        var body = await patch.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        ContractSchema.AssertValid("identity/v1/membership.schema.json", body);
        Assert.Equal("developer", JsonDocument.Parse(body).RootElement.GetProperty("role").GetString());

        var list = await member.GetAsync($"/v1/organizations/{orgId}/members");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task Last_owner_cannot_be_demoted_or_removed()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var ownerId = await UserId(owner);

        var patch = await owner.PatchAsJsonAsync(
            $"/v1/organizations/{orgId}/members/{ownerId}",
            new { role = "developer" },
            RingfourJson.Options);
        await AssertError(patch, HttpStatusCode.Conflict, "last_owner");

        var delete = await owner.DeleteAsync($"/v1/organizations/{orgId}/members/{ownerId}");
        await AssertError(delete, HttpStatusCode.Conflict, "last_owner");
    }

    [Fact]
    public async Task Concurrent_demotions_leave_at_least_one_owner()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var first = factory.CreateAuthenticatedClient(UniqueSub());
        using var second = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(first, "Acme Robotics");
        var firstId = await UserId(first);
        var secondId = await UserId(second);
        var promote = await first.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = secondId, role = "owner" },
            RingfourJson.Options);
        Assert.Equal(HttpStatusCode.Created, promote.StatusCode);

        var results = await Task.WhenAll(
            first.PatchAsJsonAsync(
                $"/v1/organizations/{orgId}/members/{firstId}",
                new { role = "developer" },
                RingfourJson.Options),
            second.PatchAsJsonAsync(
                $"/v1/organizations/{orgId}/members/{secondId}",
                new { role = "developer" },
                RingfourJson.Options));

        var statuses = results.Select(response => response.StatusCode).ToList();
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Conflict));

        using var list = JsonDocument.Parse(
            await first.GetStringAsync($"/v1/organizations/{orgId}/members"));
        var roles = list.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("role").GetString())
            .ToList();
        Assert.Contains("owner", roles);
    }

    [Fact]
    public async Task Owner_removes_member_and_membership_is_gone()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var member = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var memberId = await UserId(member);
        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = memberId, role = "operator" },
            RingfourJson.Options);

        var delete = await owner.DeleteAsync($"/v1/organizations/{orgId}/members/{memberId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var list = await member.GetAsync($"/v1/organizations/{orgId}/members");
        await AssertError(list, HttpStatusCode.NotFound, "not_found");

        var deleteAgain = await owner.DeleteAsync($"/v1/organizations/{orgId}/members/{memberId}");
        await AssertError(deleteAgain, HttpStatusCode.NotFound, "not_found");
    }
}
