using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReductionTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("GET", "/v1/organizations/00000000-0000-0000-0000-000000000001/missions")]
    [InlineData("POST", "/v1/missions")]
    [InlineData("POST", "/v1/script-revisions")]
    [InlineData("GET", "/v1/executions/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/v1/robots/00000000-0000-0000-0000-000000000001/dev-sessions")]
    [InlineData("POST", "/v1/robots/00000000-0000-0000-0000-000000000001/operations")]
    [InlineData("GET", "/v1/device/ui-connect")]
    public async Task Retired_routes_are_not_registered(string method, string path)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upgrade_preserves_users_organizations_and_memberships()
    {
        var database = "reduction_" + Guid.NewGuid().ToString("N");
        await postgres.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
            await using var db = new RingfourDbContext(new DbContextOptionsBuilder<RingfourDbContext>().UseNpgsql(connection).Options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261125081901_AP14MissionUiRemoval");
            var user = Guid.NewGuid(); var org = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,auth0_sub,created_at) VALUES ({user},{"auth0|preserved"},{now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,name,created_at) VALUES ({org},{"Preserved organization"},{now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO memberships(organization_id,user_id,role,created_at) VALUES ({org},{user},{"Owner"},{now})");
            await migrator.MigrateAsync();
            Assert.Equal("auth0|preserved", (await db.Users.SingleAsync(x => x.Id == user)).Auth0Sub);
            Assert.Equal("Preserved organization", (await db.Organizations.SingleAsync(x => x.Id == org)).Name);
            Assert.Equal(OrgRole.Owner, (await db.Memberships.SingleAsync(x => x.UserId == user && x.OrganizationId == org)).Role);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.ExecuteAsync($"DROP DATABASE \"{database}\" WITH (FORCE)");
        }
    }
}
