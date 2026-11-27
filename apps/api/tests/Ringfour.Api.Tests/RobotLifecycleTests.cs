using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RobotLifecycleTests(PostgresFixture postgres)
{
    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}"); return JsonNode.Parse(text)!.AsObject();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_releases_credentials_and_receipt_survives_offline_daemon_and_new_binding(bool delete)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        using var device = factory.CreateClient();
        var org = await OrganizationTests.CreateOrganization(owner, "Robot lifecycle");
        var robot = await RobotTests.CreateRobot(owner, org, "Disposable device");
        var (_, token) = await RobotTests.IssueToken(owner, robot);
        var (credential, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        var body = new {credential_id=credential,secret};
        var state = await Json(await device.PostAsJsonAsync("/v1/device/enrollment-state",body));
        Assert.Equal("enrolled",state["state"]!.ToString());
        var response = delete ? await owner.DeleteAsync($"/v1/robots/{robot}") : await owner.PostAsync($"/v1/robots/{robot}/unenroll",null);
        Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);
        var receipt = await Json(await device.PostAsJsonAsync("/v1/device/enrollment-state",body));
        Assert.Equal("released",receipt["state"]!.ToString());
        Assert.Equal(delete?"robot_deleted":"robot_unenrolled",receipt["reason"]!.ToString());
        ContractSchema.AssertValid("robots/v1/enrollment-state-response.schema.json",receipt.ToJsonString());
        Assert.Equal(HttpStatusCode.Unauthorized,(await device.PostAsJsonAsync("/v1/device/authenticate",body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await device.PostAsJsonAsync("/v1/device/enrollment-state",new {credential_id=credential,secret="wrong"})).StatusCode);
        if(delete)
        {
            Assert.Equal(HttpStatusCode.NotFound,(await owner.GetAsync($"/v1/robots/{robot}")).StatusCode);
            var listed=await Json(await owner.GetAsync($"/v1/organizations/{org}/robots"));
            Assert.DoesNotContain(listed["items"]!.AsArray(),item=>item!["id"]!.ToString()==robot.ToString());
            Assert.Equal(HttpStatusCode.NotFound,(await owner.PostAsync($"/v1/robots/{robot}/enrollment-tokens",null)).StatusCode);
        }
        else
        {
            Assert.Null((await Json(await owner.GetAsync($"/v1/robots/{robot}")))["enrolled_at"]);
            var (_,nextToken)=await RobotTests.IssueToken(owner,robot);
            var (nextCredential,nextSecret)=await DeviceEnrollmentTests.Enroll(factory,nextToken);
            Assert.Equal("released",(await Json(await device.PostAsJsonAsync("/v1/device/enrollment-state",body)))["state"]!.ToString());
            Assert.Equal("enrolled",(await Json(await device.PostAsJsonAsync("/v1/device/enrollment-state",new {credential_id=nextCredential,secret=nextSecret})))["state"]!.ToString());
        }
    }
    [Fact]
    public async Task Ordinary_revocation_does_not_authorize_forgetting_enrollment()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        var org=await OrganizationTests.CreateOrganization(owner,"Credential revocation");
        var robot=await RobotTests.CreateRobot(owner,org,"Device");var(_,token)=await RobotTests.IssueToken(owner,robot);
        var(id,secret)=await DeviceEnrollmentTests.Enroll(factory,token);
        await owner.PostAsync($"/v1/robots/{robot}/credentials/{id}/revoke",null);
        var receipt=await Json(await factory.CreateClient().PostAsJsonAsync("/v1/device/enrollment-state",new {credential_id=id,secret}));
        Assert.Equal("enrolled",receipt["state"]!.ToString());
    }
    [Theory]
    [InlineData("operator")]
    [InlineData("developer")]
    public async Task Only_owner_can_unenroll_or_delete(string role)
    {
        await using var factory=new ApiFactory(postgres.ConnectionString);
        using var owner=factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());using var member=factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());using var foreign=factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        var org=await OrganizationTests.CreateOrganization(owner,"Owner lifecycle");var robot=await RobotTests.CreateRobot(owner,org,"Device");
        var me=await Json(await member.GetAsync("/v1/me"));await owner.PostAsJsonAsync($"/v1/organizations/{org}/members",new {user_id=me["user"]!["id"]!.ToString(),role});
        Assert.Equal(HttpStatusCode.Forbidden,(await member.PostAsync($"/v1/robots/{robot}/unenroll",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await member.DeleteAsync($"/v1/robots/{robot}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.DeleteAsync($"/v1/robots/{robot}")).StatusCode);
    }
}
