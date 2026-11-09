using System.Net;
using System.Text.Json;
using Npgsql;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MeTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";

    private static async Task<JsonDocument> GetMe(HttpClient client)
    {
        var response = await client.GetAsync("/v1/me");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ContractSchema.AssertValid("identity/v1/me-response.schema.json", body);
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task First_request_creates_user_then_lookup_is_stable()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var sub = UniqueSub();

        using var first = await GetMe(factory.CreateAuthenticatedClient(sub));
        var id = first.RootElement.GetProperty("user").GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Empty(first.RootElement.GetProperty("memberships").EnumerateArray());

        using var second = await GetMe(factory.CreateAuthenticatedClient(sub));
        Assert.Equal(id, second.RootElement.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Identity_is_stable_across_email_changes()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var sub = UniqueSub();

        using var first = await GetMe(factory.CreateAuthenticatedClient(sub, "first@example.com"));
        using var second = await GetMe(factory.CreateAuthenticatedClient(sub, "moved@example.com"));

        Assert.Equal(
            first.RootElement.GetProperty("user").GetProperty("id").GetString(),
            second.RootElement.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Concurrent_first_requests_create_exactly_one_user()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var sub = UniqueSub();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ =>
                factory.CreateAuthenticatedClient(sub).GetAsync("/v1/me")));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ids = new HashSet<string>();
        foreach (var response in responses)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            ids.Add(json.RootElement.GetProperty("user").GetProperty("id").GetString()!);
        }
        Assert.Single(ids);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM users WHERE auth0_sub = @sub", connection);
        command.Parameters.AddWithValue("sub", sub);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}
