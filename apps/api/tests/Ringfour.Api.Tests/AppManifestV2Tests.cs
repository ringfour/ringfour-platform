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

    [Fact]
    public void BoundedFloatsPreserveCanonicalDigestConfigurationEnvironmentAndSettings()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Contracts, "fixtures/apps/v2/floating-point.json")))!;
        var manifest = AppManifest.Validate(fixture["manifest"]!.ToJsonString());
        Assert.Equal(fixture["canonical_digest"]!.GetValue<string>(), "sha256:" + AppProtocol.Digest(manifest));

        var configuration = fixture["configuration_values"]!;
        Assert.True(JsonNode.DeepEquals(
            AppInstallationEndpoints.ValidateConfiguration(manifest, configuration), configuration));
        var environment = AppManifest.Environment(manifest, configuration);
        foreach (var (name, value) in fixture["expected_environment"]!.AsObject())
            Assert.Equal(value!.GetValue<string>(), environment[name]!.GetValue<string>());

        var settings = AppManifest.ValidateRuntimeSettings(manifest, fixture["runtime_settings_values"]!);
        Assert.True(JsonNode.DeepEquals(settings, fixture["runtime_settings_values"]));

        foreach (var invalid in new JsonNode?[]
        {
            JsonValue.Create(true), JsonValue.Create("0.2"), JsonValue.Create(-1.000001),
            JsonValue.Create(1.000001), JsonValue.Create(9007199254740992d)
        })
        {
            var values = configuration.DeepClone().AsObject();
            values["ratio"] = invalid?.DeepClone();
            Assert.Throws<ApiException>(() => AppInstallationEndpoints.ValidateConfiguration(manifest, values));
        }
        Assert.Throws<ApiException>(() => AppProtocol.Validate("{\"value\":1e400}", "request_start"));
        Assert.Throws<ApiException>(() => AppProtocol.Validate("{\"value\":1e-400}", "request_start"));
    }
}
