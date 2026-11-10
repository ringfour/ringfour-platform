using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DeviceEnrollmentTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";

    internal static async Task<(Guid CredentialId, string Secret)> Enroll(
        ApiFactory factory, string token)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/enroll", new { token }, RingfourJson.Options);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ContractSchema.AssertValid("robots/v1/enroll-response.schema.json", body);
        using var document = JsonDocument.Parse(body);
        return (
            document.RootElement.GetProperty("credential_id").GetGuid(),
            document.RootElement.GetProperty("credential_secret").GetString()!);
    }

    internal static async Task AssertAuthenticates(
        ApiFactory factory, Guid credentialId, string secret, Guid expectedRobotId)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/authenticate",
            new { credential_id = credentialId, secret },
            RingfourJson.Options);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ContractSchema.AssertValid("robots/v1/device-authenticate-response.schema.json", body);
        Assert.Equal(
            expectedRobotId,
            JsonDocument.Parse(body).RootElement.GetProperty("robot_id").GetGuid());
    }

    [Fact]
    public async Task Enroll_consumes_token_and_records_enrollment()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await RobotTests.CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await RobotTests.IssueToken(owner, robotId);

        var (credentialId, secret) = await Enroll(factory, token);

        // enrolled_at is stamped and the enrolled event is device-attributed.
        var detail = JsonDocument.Parse(await owner.GetStringAsync($"/v1/robots/{robotId}"));
        Assert.Equal(JsonValueKind.String, detail.RootElement.GetProperty("enrolled_at").ValueKind);
        var audit = await postgres.QueryAsync(
            "SELECT actor_user_id FROM robot_audit_events" +
            " WHERE robot_id = @robotId AND type = 'enrolled'",
            ("@robotId", robotId));
        var row = Assert.Single(audit);
        Assert.Equal(DBNull.Value, row[0]);

        await AssertAuthenticates(factory, credentialId, secret, robotId);

        // Credential metadata lists the new credential without any secret material.
        var list = await owner.GetStringAsync($"/v1/robots/{robotId}/credentials");
        ContractSchema.AssertValid("robots/v1/credential-list-response.schema.json", list);
        Assert.DoesNotContain("secret", list);
        Assert.DoesNotContain("hash", list);
        Assert.Equal(
            credentialId,
            JsonDocument.Parse(list).RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Enroll_rejects_reuse_expired_and_garbage_tokens()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await RobotTests.CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await RobotTests.IssueToken(owner, robotId);

        await Enroll(factory, token);

        var reuse = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/enroll", new { token }, RingfourJson.Options);
        await RobotTests.AssertError(reuse, HttpStatusCode.Unauthorized, "invalid_token");

        var garbage = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/enroll", new { token = "rft_not-a-real-token" }, RingfourJson.Options);
        await RobotTests.AssertError(garbage, HttpStatusCode.Unauthorized, "invalid_token");

        // Expired before it was ever consumed: inserted with a past expiry.
        var expiredToken = "rft_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(expiredToken))).ToLowerInvariant();
        await postgres.ExecuteAsync(
            """
            INSERT INTO enrollment_tokens
                (id, robot_id, token_hash, issued_by_user_id, created_at, expires_at, consumed_at)
            VALUES
                (@id, @robotId, @hash, @userId, now() - interval '20 minutes', now() - interval '5 minutes', NULL)
            """,
            ("@id", Guid.NewGuid()),
            ("@robotId", robotId),
            ("@hash", hash),
            ("@userId", await RobotTests.UserId(owner)));
        var expired = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/enroll", new { token = expiredToken }, RingfourJson.Options);
        await RobotTests.AssertError(expired, HttpStatusCode.Unauthorized, "invalid_token");
    }

    [Fact]
    public async Task Concurrent_enrollment_has_exactly_one_winner()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await RobotTests.CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await RobotTests.IssueToken(owner, robotId);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => factory.CreateClient().PostAsJsonAsync(
                "/v1/device/enroll", new { token }, RingfourJson.Options))
            .ToArray();
        var responses = await Task.WhenAll(attempts);

        Assert.Equal(
            1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(
            7, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));

        // Exactly one credential exists: the loser never created a stranded one.
        var credentials = await postgres.QueryAsync(
            "SELECT id FROM robot_credentials WHERE robot_id = @robotId",
            ("@robotId", robotId));
        Assert.Single(credentials);
    }

    [Fact]
    public async Task Authenticate_rejects_unknown_id_and_wrong_secret()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await RobotTests.CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await RobotTests.IssueToken(owner, robotId);
        var (credentialId, _) = await Enroll(factory, token);

        var unknown = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/authenticate",
            new { credential_id = Guid.NewGuid(), secret = "rfs_whatever" },
            RingfourJson.Options);
        await RobotTests.AssertError(unknown, HttpStatusCode.Unauthorized, "invalid_token");

        var wrong = await factory.CreateClient().PostAsJsonAsync(
            "/v1/device/authenticate",
            new
            {
                credential_id = credentialId,
                secret = "rfs_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            },
            RingfourJson.Options);
        await RobotTests.AssertError(wrong, HttpStatusCode.Unauthorized, "invalid_token");
    }
}
