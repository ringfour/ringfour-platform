using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OrganizationDeletionTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";
    private static async Task<Guid> UserId(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/v1/me"));
        return me.RootElement.GetProperty("user").GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_can_delete_with_or_without_other_members_and_only_that_organization_loses_access(bool addMember)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var other = factory.CreateAuthenticatedClient(UniqueSub());
        var org = await OrganizationTests.CreateOrganization(owner, "Delete target");
        var retained = await OrganizationTests.CreateOrganization(owner, "Retained organization");
        var ownerId = await UserId(owner);
        if (addMember)
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/v1/organizations/{org}/members", new { user_id = await UserId(other), role = "operator" }, RingfourJson.Options)).StatusCode);
        var robot = await RobotTests.CreateRobot(owner, org, "Device to disconnect");
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var (_, unusedToken) = await RobotTests.IssueToken(owner, robot);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            db.OrganizationInvitations.Add(new OrganizationInvitation { Id = Guid.NewGuid(), OrganizationId = org, Email = "fixture@example.test", NormalizedEmail = "FIXTURE@EXAMPLE.TEST", Role = OrgRole.Operator, InvitedByUserId = ownerId, TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/v1/organizations/{org}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/v1/robots/{robot}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1/organizations/{org}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/v1/organizations/{org}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/v1/device/authenticate", new { credential_id = credential, secret }, RingfourJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/v1/device/enroll", new { token = unusedToken }, RingfourJson.Options)).StatusCode);
        using var orgs = JsonDocument.Parse(await owner.GetStringAsync("/v1/organizations"));
        Assert.DoesNotContain(orgs.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == org);
        Assert.Contains(orgs.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == retained);
        Assert.Equal(ownerId, await UserId(owner));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            Assert.NotNull((await db.Organizations.SingleAsync(o => o.Id == org)).DeletedAt);
            Assert.False(await db.Memberships.AnyAsync(m => m.OrganizationId == org));
            Assert.True(await db.Robots.AnyAsync(r => r.Id == robot));
            Assert.False(await db.RobotCredentials.AnyAsync(c => c.RobotId == robot && c.RevokedAt == null));
            Assert.False(await db.OrganizationInvitations.AnyAsync(i => i.OrganizationId == org && i.AcceptedAt == null && i.RevokedAt == null));
            // Even a credential created by an already in-flight rotation cannot
            // authenticate a robot whose organization was deleted.
            await db.RobotCredentials.Where(c => c.Id == credential).ExecuteUpdateAsync(update => update.SetProperty(c => c.RevokedAt, (DateTimeOffset?)null));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/v1/device/authenticate", new { credential_id = credential, secret }, RingfourJson.Options)).StatusCode);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("developer")]
    public async Task Non_owner_cannot_delete_an_organization(string role)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(UniqueSub());
        using var member = factory.CreateAuthenticatedClient(UniqueSub());
        using var outsider = factory.CreateAuthenticatedClient(UniqueSub());
        var org = await OrganizationTests.CreateOrganization(owner, "Owner deletion only");
        await owner.PostAsJsonAsync($"/v1/organizations/{org}/members", new { user_id = await UserId(member), role }, RingfourJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/v1/organizations/{org}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/v1/organizations/{org}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().DeleteAsync($"/v1/organizations/{org}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/v1/organizations/{org}/members")).StatusCode);
    }
}
