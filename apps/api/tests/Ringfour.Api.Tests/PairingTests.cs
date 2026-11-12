using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PairingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Pairing_migrates_from_connectivity_schema_with_existing_data()
    {
        var database = $"pairing_{Guid.NewGuid():N}";
        await postgres.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
            var options = new DbContextOptionsBuilder<RingfourDbContext>().UseNpgsql(connection).Options;
            await using var db = new RingfourDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261110071936_Connectivity");
            var userId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users (id,auth0_sub,created_at) VALUES ({userId},{"auth0|migration"},{DateTimeOffset.UtcNow})");
            await migrator.MigrateAsync();
            Assert.True(await db.Users.AnyAsync(user => user.Id == userId));
            Assert.Empty(await db.DevicePairingRequests.ToListAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.ExecuteAsync($"DROP DATABASE \"{database}\" WITH (FORCE)");
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(13)).Replace('0', 'A').Replace('1', 'B').Replace('8', 'C').Replace('9', 'D');
    private static string NewSecret() => "rfs_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static async Task<HttpResponseMessage> Start(HttpClient client, Guid id, string code, string secret) =>
        await client.PostAsJsonAsync("/v1/device/pairing-requests", new { request_id = id, code_hash = Hash(code), credential_hash = Hash(secret) }, RingfourJson.Options);
    private static async Task<HttpResponseMessage> Poll(HttpClient client, Guid id, string secret) =>
        await client.PostAsJsonAsync($"/v1/device/pairing-requests/{id}/poll", new { credential_secret = secret }, RingfourJson.Options);

    [Fact]
    public async Task Owner_claim_is_single_use_and_daemon_can_recover_poll_after_restart()
    {
        var id = Guid.NewGuid(); var code = NewCode(); var secret = NewSecret();
        Guid orgId; Guid robotId; Guid credentialId;
        await using (var factory = new ApiFactory(postgres.ConnectionString))
        {
            var device = factory.CreateClient();
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            orgId = await OrganizationTests.CreateOrganization(owner, "pairing org");
            Assert.Equal(HttpStatusCode.OK, (await Start(device, id, code, secret)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Start(device, id, code, secret)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Start(device, id, NewCode(), secret)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Poll(device, id, NewSecret())).StatusCode);
            var pending = await (await Poll(device, id, secret)).Content.ReadAsStringAsync();
            ContractSchema.AssertValid("pairing/v1/poll-response.schema.json", pending);
            Assert.Equal("pending", JsonDocument.Parse(pending).RootElement.GetProperty("state").GetString());
            var claim = await owner.PostAsJsonAsync($"/v1/organizations/{orgId}/pairing-claims", new { code, name = "base101" }, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            robotId = JsonDocument.Parse(await claim.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/organizations/{orgId}/pairing-claims", new { code, name = "duplicate" }, RingfourJson.Options)).StatusCode);
            var body = JsonDocument.Parse(await (await Poll(device, id, secret)).Content.ReadAsStringAsync()).RootElement;
            credentialId = body.GetProperty("credential_id").GetGuid();
            Assert.Equal(robotId, body.GetProperty("robot_id").GetGuid());
            await DeviceEnrollmentTests.AssertAuthenticates(factory, credentialId, secret, robotId);
            var row = Assert.Single(await postgres.QueryAsync("SELECT code_hash, credential_hash FROM device_pairing_requests WHERE id=@id", ("@id", id)));
            Assert.Equal(Hash(code), row[0]); Assert.Equal(Hash(secret), row[1]);
        }
        await using var restarted = new ApiFactory(postgres.ConnectionString);
        var recovered = JsonDocument.Parse(await (await Poll(restarted.CreateClient(), id, secret)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(robotId, recovered.GetProperty("robot_id").GetGuid());
        Assert.Equal(credentialId, recovered.GetProperty("credential_id").GetGuid());
    }

    [Fact]
    public async Task Unauthorized_expired_and_competing_claims_are_rejected()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var orgId = await OrganizationTests.CreateOrganization(owner, "pairing org");
        var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var operatorId = await RobotTests.UserId(outsider);
        await owner.PostAsJsonAsync($"/v1/organizations/{orgId}/members", new { user_id = operatorId, role = "operator" }, RingfourJson.Options);
        var device = factory.CreateClient(); var code = NewCode(); var secret = NewSecret(); var id = Guid.NewGuid();
        await Start(device, id, code, secret);
        var url = $"/v1/organizations/{orgId}/pairing-claims";
        var payload = new { code, name = "robot" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync(url, payload, RingfourJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(url, payload, RingfourJson.Options)).StatusCode);
        var races = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => owner.PostAsJsonAsync(url, payload, RingfourJson.Options)));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.Conflict);
        var expiredId = Guid.NewGuid(); var expiredCode = NewCode(); var expiredSecret = NewSecret();
        await Start(device, expiredId, expiredCode, expiredSecret);
        await postgres.ExecuteAsync("UPDATE device_pairing_requests SET expires_at=now()-interval '1 minute' WHERE id=@id", ("@id", expiredId));
        Assert.Equal(HttpStatusCode.Gone, (await Poll(device, expiredId, expiredSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(url, new { code = expiredCode, name = "late" }, RingfourJson.Options)).StatusCode);
        var tooLarge = new StringContent(new string('a', 4097), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await device.PostAsync("/v1/device/pairing-requests", tooLarge)).StatusCode);
    }

    [Fact]
    public async Task Existing_robot_must_belong_to_owner_and_revocation_invalidates_poll()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var other = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(owner, "owner org");
        var otherOrg = await OrganizationTests.CreateOrganization(other, "other org");
        var robot = await RobotTests.CreateRobot(owner, org, "existing");
        var otherRobot = await RobotTests.CreateRobot(other, otherOrg, "other");
        var code = NewCode(); var secret = NewSecret(); var id = Guid.NewGuid();
        var device = factory.CreateClient();
        await Start(device, id, code, secret);
        var path = $"/v1/organizations/{org}/pairing-claims";
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(path, new { code, robot_id = otherRobot }, RingfourJson.Options)).StatusCode);
        var claim = await owner.PostAsJsonAsync(path, new { code, robot_id = robot }, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var poll = JsonDocument.Parse(await (await Poll(device, id, secret)).Content.ReadAsStringAsync()).RootElement;
        var credential = poll.GetProperty("credential_id").GetGuid();
        Assert.Equal(robot, poll.GetProperty("robot_id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/v1/robots/{robot}/credentials/{credential}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Poll(device, id, secret)).StatusCode);
    }

    [Fact]
    public async Task Pairing_requests_are_rate_limited()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var client = factory.CreateClient();
        HttpResponseMessage? result = null;
        for (var i = 0; i < 121; i++)
            result = await client.PostAsJsonAsync("/v1/device/pairing-requests", new { request_id = Guid.Empty }, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, result!.StatusCode);
        var body = await result.Content.ReadAsStringAsync();
        Assert.Equal("rate_limited", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
