using System.Text.Json.Nodes;
using Ringfour.Api.Apps;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Tests;

public sealed class AppManifestV2Tests
{
    private static readonly string Contracts = Path.Combine(AppContext.BaseDirectory, "contracts");

    [Fact]
    public void ValidatesV2AndResolvesEnvironmentValuesLiterally()
    {
        var raw = File.ReadAllText(Path.Combine(Contracts, "fixtures/apps/v2/manifest.json"));
        var manifest = AppManifest.Validate(raw);
        var environment = AppManifest.Environment(manifest, JsonNode.Parse("{\"sample_count\":3}")!);
        Assert.Equal("3", environment["SAMPLE_COUNT"]!.GetValue<string>());
        Assert.Empty(AppManifest.Environment(manifest, JsonNode.Parse("{}")!));
        manifest["configuration"]!["schema"]!["properties"]!["enabled"] = new JsonObject { ["type"] = "boolean" };
        manifest["configuration"]!["environment"]!["APP_ENABLED"] = "enabled";
        var configuration = JsonNode.Parse("{\"sample_count\":3,\"enabled\":false}")!;
        Assert.Equal("false", AppManifest.Environment(manifest, configuration)["APP_ENABLED"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("RINGFOUR_CONTROL")]
    [InlineData("PYTHONPATH")]
    public void RefusesReservedEnvironmentNames(string name)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Contracts, "fixtures/apps/v2/manifest.json")))!.AsObject();
        manifest["configuration"]!["environment"] = new JsonObject { [name] = "sample_count" };
        Assert.Throws<ApiException>(() => AppManifest.Validate(manifest.ToJsonString()));
    }

    [Fact]
    public void RefusesNulInValuesMappedToEnvironment()
    {
        var manifest = AppManifest.Validate(File.ReadAllText(Path.Combine(Contracts, "fixtures/apps/v2/manifest.json")));
        manifest["configuration"]!["schema"]!["properties"]!["note"] = new JsonObject { ["type"] = "string" };
        manifest["configuration"]!["environment"]!["APP_NOTE"] = "note";
        Assert.Throws<ApiException>(() => AppInstallationEndpoints.ValidateConfiguration(
            manifest, JsonNode.Parse("{\"sample_count\":3,\"note\":\"bad\\u0000value\"}")!));
    }
}
