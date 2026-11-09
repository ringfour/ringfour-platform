using System.Net;
using System.Text.Json;

namespace Ringfour.Api.Tests;

public sealed class HealthLiveTests
{
    [Fact]
    public async Task Liveness_reports_status_and_version_without_dependency_checks()
    {
        // No database behind this connection string; liveness must not care.
        await using var factory = new ApiFactory(
            "Host=localhost;Port=1;Database=ringfour;Username=ringfour;Password=ringfour_dev",
            migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ContractSchema.AssertValid("health/v1/liveness-response.schema.json", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("0.1.0", json.RootElement.GetProperty("version").GetString());
    }
}
