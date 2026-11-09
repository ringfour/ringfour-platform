using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OrganizationTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";

    internal static async Task<Guid> CreateOrganization(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/v1/organizations", new { name }, RingfourJson.Options);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ContractSchema.AssertValid("identity/v1/organization.schema.json", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Create_organization_grants_owner_membership_atomically()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(UniqueSub());

        var orgId = await CreateOrganization(client, "Acme Robotics");

        var me = await client.GetAsync("/v1/me");
        var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var memberships = meBody.RootElement.GetProperty("memberships").EnumerateArray().ToList();
        var membership = Assert.Single(memberships);
        Assert.Equal(orgId, membership.GetProperty("organization_id").GetGuid());
        Assert.Equal("owner", membership.GetProperty("role").GetString());

        var list = await client.GetAsync("/v1/organizations");
        var listBody = await list.Content.ReadAsStringAsync();
        ContractSchema.AssertValid("identity/v1/organization-list-response.schema.json", listBody);
        var item = Assert.Single(
            JsonDocument.Parse(listBody).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(orgId, item.GetProperty("id").GetGuid());
        Assert.Equal("owner", item.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Blank_organization_name_is_rejected()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(UniqueSub());

        var response = await client.PostAsJsonAsync(
            "/v1/organizations", new { name = "   " }, RingfourJson.Options);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ContractSchema.AssertValid("errors/v1/error.schema.json", body);
        Assert.Equal(
            "invalid_input",
            JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Organizations_paginate_with_opaque_cursor()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(UniqueSub());
        var created = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            created.Add(await CreateOrganization(client, $"Org {i}"));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/v1/organizations?limit=2{(cursor is null ? "" : $"&cursor={cursor}")}";
            using var page = JsonDocument.Parse(
                await client.GetStringAsync(url));
            var items = page.RootElement.GetProperty("items");
            seen.AddRange(items.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.RootElement.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null
                : page.RootElement.GetProperty("next_cursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(2, pages);
        Assert.Equal(created.OrderBy(id => id), seen.OrderBy(id => id));
    }
}
