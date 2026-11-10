using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ringfour.Api.Tests;

public sealed class ApiFactory(
    string connectionString,
    bool migrateOnStartup = true,
    bool productionEnvironment = false,
    bool includeTestAuth = true)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (productionEnvironment)
        {
            builder.UseEnvironment("Production");
        }
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Database:MigrateOnStartup"] = migrateOnStartup ? "true" : "false",
                // The sweeper is driven manually in connectivity tests; the
                // hosted loop staying quiet keeps dispatch assertions exact.
                ["Connectivity:SweepIntervalSeconds"] = "3600",
            };
            if (includeTestAuth)
            {
                values["Auth:Test:Issuer"] = TestAuth.Issuer;
                values["Auth:Test:Audience"] = TestAuth.Audience;
                values["Auth:Test:RsaPrivateKeyPem"] = TestAuth.PrivateKeyPem;
            }
            config.AddInMemoryCollection(values);
        });
    }

    public HttpClient CreateAuthenticatedClient(string subject, string? email = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAuth.CreateToken(subject, email));
        return client;
    }
}
