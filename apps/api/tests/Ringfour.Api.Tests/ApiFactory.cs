using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Apps;

namespace Ringfour.Api.Tests;

public sealed class ApiFactory(
    string connectionString,
    bool migrateOnStartup = true,
    bool productionEnvironment = false,
    bool includeTestAuth = true,
    string? storageRoot = null,
    IReadOnlyDictionary<string, string?>? extraConfiguration = null,
    bool automaticAppDispatch = false)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (productionEnvironment)
        {
            builder.UseEnvironment("Production");
        }
        if (!automaticAppDispatch)
            builder.ConfigureTestServices(services =>
            {
                var worker = services.SingleOrDefault(item => item.ImplementationType == typeof(AppInstallationWorker));
                if (worker is not null) services.Remove(worker);
            });
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Database:MigrateOnStartup"] = migrateOnStartup ? "true" : "false",
                ["Apps:RecommendedAgentVersion"] = "1.0.0",
                ["Apps:RecommendedDaemonVersion"] = "1.0.0",
            };
            if (includeTestAuth)
            {
                values["Auth:Test:Issuer"] = TestAuth.Issuer;
                values["Auth:Test:Audience"] = TestAuth.Audience;
                values["Auth:Test:RsaPrivateKeyPem"] = TestAuth.PrivateKeyPem;
            }
            values["Storage:Backend"] = "filesystem";
            values["Storage:FilesystemRoot"] = storageRoot ?? Path.GetTempPath();
            if (extraConfiguration is not null)
                foreach (var pair in extraConfiguration) values[pair.Key] = pair.Value;
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
