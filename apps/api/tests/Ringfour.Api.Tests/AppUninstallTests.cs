using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

public sealed partial class AppInstallationTests
{
    [Fact]
    public async Task Uninstall_is_negotiated_idempotent_blocks_start_and_reinstall_has_new_identity()
    {
        await using var s = await Setup(uninstallSupported: true);
        var path = $"/v1/robots/{s.Robot}/app-installations";
        var install = await Json(await Post(s.Owner, path, s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations", Applied(s, await Command(s, install))));
        var resource = $"/v1/app-installations/{install["installation_id"]}";
        var binding = (await Json(await s.Owner.GetAsync(resource)))["installed"]!;
        var body = new JsonObject { ["expected_revision_id"] = binding["revision_id"]!.DeepClone(),
            ["expected_configuration_revision_id"] = binding["configuration_revision_id"]!.DeepClone() };
        using var stranger = s.Factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
        await Error(await Post(stranger, resource + "/uninstall", body), 404, "not_found");
        var stale = body.DeepClone(); stale["expected_revision_id"] = Guid.NewGuid().ToString();
        await Error(await Post(s.Owner, resource + "/uninstall", stale), 409, "stale_installation");
        var key = Guid.NewGuid().ToString();
        var accepted = await Json(await Post(s.Owner, resource + "/uninstall", body, key));
        Assert.True(JsonNode.DeepEquals(accepted, await Json(await Post(s.Owner, resource + "/uninstall", body, key))));
        await Error(await Post(s.Owner, resource + "/start", body), 409, "execution_conflict");
        var command = await Command(s, accepted);
        Assert.Equal("app.uninstall", command["kind"]!.ToString());
        var report = new JsonObject { ["app_protocol_version"] = 1, ["deployment_namespace"] = s.Namespace.ToString(),
            ["report_id"] = Guid.NewGuid().ToString(), ["report_sequence"] = ++s.Sequence, ["runs"] = new JsonArray(),
            ["operations"] = new JsonArray(new JsonObject { ["operation_id"] = accepted["operation_id"]!.DeepClone(),
                ["installation_id"] = install["installation_id"]!.DeepClone(), ["run_id"] = null, ["kind"] = "uninstall",
                ["state"] = "succeeded", ["phase"] = "applied", ["observed_at"] = Ringfour.Api.Apps.AppProtocol.Timestamp(DateTimeOffset.UtcNow), ["failure"] = null }),
            ["installations"] = new JsonArray(new JsonObject { ["installation_id"] = install["installation_id"]!.DeepClone(),
                ["installed"] = null, ["candidate"] = null, ["observed_at"] = Ringfour.Api.Apps.AppProtocol.Timestamp(DateTimeOffset.UtcNow) }) };
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations", report));
        var removed = await Json(await s.Owner.GetAsync(resource));
        Assert.Null(removed["installed"]); Assert.NotNull(removed["removed_at"]);
        var appliedHistory = await Json(await s.Owner.GetAsync(resource + "/configuration-revisions"));
        Assert.Contains(appliedHistory["items"]!.AsArray(), item => item!["id"]!.ToString() == binding["configuration_revision_id"]!.ToString());
        await Error(await stranger.GetAsync(resource + "/configuration-revisions"), 404, "not_found");
        var replacement = await Json(await Post(s.Owner, path, s.Input));
        Assert.NotEqual(install["installation_id"]!.ToString(), replacement["installation_id"]!.ToString());
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations", report));
        await Json(await s.Owner.GetAsync(resource + $"/configuration-revisions/{binding["configuration_revision_id"]}"));
    }

    [Fact]
    public async Task Uninstall_requires_negotiated_support()
    {
        await using var s = await Setup();
        var install = await Json(await Post(s.Owner, $"/v1/robots/{s.Robot}/app-installations", s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations", Applied(s, await Command(s, install))));
        var binding = (await Json(await s.Owner.GetAsync($"/v1/app-installations/{install["installation_id"]}")))["installed"]!;
        await Error(await Post(s.Owner, $"/v1/app-installations/{install["installation_id"]}/uninstall", new JsonObject {
            ["expected_revision_id"] = binding["revision_id"]!.DeepClone(), ["expected_configuration_revision_id"] = binding["configuration_revision_id"]!.DeepClone() }), 409, "daemon_upgrade_required");
    }
}
