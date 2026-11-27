using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Ringfour.Api.Connectivity;

namespace Ringfour.Api.Tests;

public sealed partial class AppInstallationTests
{
    private static async Task DeletionBlocked(HttpClient client, Guid organization)
    {
        var response = await client.DeleteAsync($"/v1/organizations/{organization}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        ContractSchema.AssertValid("errors/v1/error.schema.json", body);
        Assert.Equal("organization_has_apps", JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Organization_deletion_requires_App_cleanup_and_then_closes_device_access()
    {
        await using var s = await Setup(uninstallSupported: true);
        var accepted = await Json(await Post(s.Owner, $"/v1/robots/{s.Robot}/app-installations", s.Input));
        await DeletionBlocked(s.Owner, s.Organization); // initial install is pending
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations", Applied(s, await Command(s, accepted))));
        var installed = await Json(await s.Owner.GetAsync("/v1/app-installations/" + accepted["installation_id"]));
        var binding = installed["installed"]!;
        await DeletionBlocked(s.Owner, s.Organization); // installed App remains
        var resource = "/v1/app-installations/" + accepted["installation_id"];
        var uninstall = await Json(await Post(s.Owner, resource + "/uninstall", new JsonObject { ["expected_revision_id"] = binding["revision_id"]!.DeepClone(), ["expected_configuration_revision_id"] = binding["configuration_revision_id"]!.DeepClone() }));
        await DeletionBlocked(s.Owner, s.Organization); // uninstall still needs device acknowledgement
        var command = await Command(s, uninstall);
        var now = Ringfour.Api.Apps.AppProtocol.Timestamp(DateTimeOffset.UtcNow);
        var removed = new JsonObject {
            ["app_protocol_version"] = 1, ["deployment_namespace"] = s.Namespace.ToString(),
            ["report_id"] = Guid.NewGuid().ToString(), ["report_sequence"] = ++s.Sequence, ["runs"] = new JsonArray(),
            ["operations"] = new JsonArray(new JsonObject { ["operation_id"] = uninstall["operation_id"]!.DeepClone(),
                ["installation_id"] = accepted["installation_id"]!.DeepClone(), ["run_id"] = null, ["kind"] = "uninstall",
                ["state"] = "succeeded", ["phase"] = "applied", ["observed_at"] = now, ["failure"] = null }),
            ["installations"] = new JsonArray(new JsonObject { ["installation_id"] = accepted["installation_id"]!.DeepClone(),
                ["installed"] = null, ["candidate"] = null, ["observed_at"] = now }) };
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations", removed));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/v1/organizations/{s.Organization}")).StatusCode);
        Assert.Null(s.Factory.Services.GetRequiredService<DeviceConnectionManager>().Get(s.Robot));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(resource)).StatusCode);
    }

    [Fact]
    public async Task Deleting_publisher_hides_Store_listing_but_preserves_another_organizations_import()
    {
        await using var s = await Setup();
        s.Factory.Services.GetRequiredService<IConfiguration>()["Apps:OfficialPublisherOrganizationId"] = s.Organization.ToString();
        var manifest = s.Revision["manifest"]!.DeepClone();
        manifest["app_key"] = "official-" + Guid.NewGuid().ToString("N");
        var listed = await Json(await Post(s.Owner, $"/v1/organizations/{s.Organization}/app-revisions", new JsonObject { ["manifest"] = manifest, ["visibility"] = "listed" }));
        var revision = Guid.Parse(listed["id"]!.ToString());
        var app = Guid.Parse(listed["app_id"]!.ToString());
        using var consumer = s.Factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        using var outsider = s.Factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        var consumerOrg = await OrganizationTests.CreateOrganization(consumer, "Existing import holder");
        var outsiderOrg = await OrganizationTests.CreateOrganization(outsider, "Fresh browser");
        await Json(await Post(consumer, $"/v1/organizations/{consumerOrg}/app-imports", new JsonObject { ["revision_id"] = revision.ToString(), ["share_id"] = null }));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/v1/organizations/{s.Organization}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await consumer.GetAsync($"/v1/app-revisions/{revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/app-revisions/{revision}")).StatusCode);
        var store = await Json(await outsider.GetAsync($"/v1/store/apps?organization_id={outsiderOrg}"));
        Assert.DoesNotContain(store["items"]!.AsArray(), item => item!["id"]!.ToString() == app.ToString());
    }
}
