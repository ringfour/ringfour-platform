using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class IdentityAuthTests(PostgresFixture postgres)
{
    private async Task AssertUnauthenticated(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        ContractSchema.AssertValid("errors/v1/error.schema.json", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            "unauthenticated", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task AssertTokenRejected(string token, string because)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await AssertUnauthenticated(await client.GetAsync("/v1/me"));
        Assert.True(true, because);
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        await AssertUnauthenticated(await client.GetAsync("/v1/me"));
    }

    [Fact]
    public async Task Malformed_token_is_rejected() =>
        await AssertTokenRejected("not-a-jwt", "malformed");

    [Fact]
    public async Task Wrong_issuer_is_rejected() =>
        await AssertTokenRejected(
            TestAuth.CreateToken("auth0|wrong-issuer", issuer: "https://evil.example.com/"),
            "issuer");

    [Fact]
    public async Task Wrong_audience_is_rejected() =>
        await AssertTokenRejected(
            TestAuth.CreateToken("auth0|wrong-audience", audience: "https://api.other.example"),
            "audience");

    [Fact]
    public async Task Expired_token_is_rejected() =>
        await AssertTokenRejected(
            TestAuth.CreateToken(
                "auth0|expired", expires: DateTimeOffset.UtcNow.AddMinutes(-30)),
            "expiry");

    [Fact]
    public async Task Wrong_algorithm_is_rejected() =>
        await AssertTokenRejected(
            TestAuth.CreateToken("auth0|hs256", algorithm: SecurityAlgorithms.HmacSha256),
            "algorithm");

    [Fact]
    public async Task Wrong_signature_is_rejected() =>
        await AssertTokenRejected(
            TestAuth.CreateToken("auth0|forged", signingKey: RSA.Create(2048)),
            "signature");

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAuthenticatedClient("auth0|valid-token");

        var response = await client.GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
