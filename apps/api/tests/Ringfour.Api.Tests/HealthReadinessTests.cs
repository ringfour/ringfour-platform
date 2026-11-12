using System.Net;
using System.Text.Json;
using Npgsql;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HealthReadinessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Readiness_reports_ok_when_postgresql_is_reachable()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ContractSchema.AssertValid("health/v1/readiness-response.schema.json", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "ok",
            json.RootElement.GetProperty("checks").GetProperty("postgresql").GetString());
        Assert.Equal("ok", json.RootElement.GetProperty("checks").GetProperty("storage").GetString());
    }

    [Fact]
    public async Task Readiness_fails_when_storage_is_misconfigured()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: "relative-storage-is-invalid");
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("fail", body.RootElement.GetProperty("checks").GetProperty("storage").GetString());
        Assert.Equal("ok", body.RootElement.GetProperty("checks").GetProperty("postgresql").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Startup_applies_ef_migrations_to_an_empty_database()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        using var liveResponse = await client.GetAsync("/health/live");
        liveResponse.EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"",
            connection);
        var migrations = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            migrations.Add(reader.GetString(0));
        }

        Assert.Contains(migrations, id => id?.EndsWith("_InitialCreate") == true);
    }
}
