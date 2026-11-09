using System.Net;
using System.Text.Json;

namespace Ringfour.Api.Tests;

public sealed class ReadinessFailureTests
{
    [Fact]
    public async Task Readiness_returns_503_when_postgresql_is_unavailable()
    {
        // Port 1 accepts no connections; readiness must report fail with HTTP 503.
        await using var factory = new ApiFactory(
            "Host=localhost;Port=1;Database=ringfour;Username=ringfour;Password=ringfour_dev;Timeout=5",
            migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ContractSchema.AssertValid("health/v1/readiness-response.schema.json", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("fail", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "fail",
            json.RootElement.GetProperty("checks").GetProperty("postgresql").GetString());
    }
}
