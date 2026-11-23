using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Ringfour.Api.Tests;

// Pure cross-language contract checks. No API factory, database, device or auth bypass.
public sealed class AppContractFixtureTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly Lazy<bool> Registered = new(() =>
    {
        foreach (var path in new[] {
            "apps/v1/manifest.schema.json", "errors/v2/app-error.schema.json",
            "apps/v1/api.schema.json", "apps/v1/requests.schema.json",
            "interactions/v2/app-interaction.schema.json", "artifacts/v2/app-artifact.schema.json",
            "logbook/v3/app-history-page.schema.json", "device/v1/command.schema.json",
            "device/apps/v1/protocol.schema.json" })
            JsonSchema.FromText(File.ReadAllText(Path.Combine(Root, "schemas", path)));
        return true;
    });

    [Theory]
    [InlineData("apps/v1/api.json", true)]
    [InlineData("apps/v1/requests.json", true)]
    [InlineData("apps/v1/errors.json", true)]
    [InlineData("apps/v1/api-rejected.json", false)]
    [InlineData("device/apps/v1/positive.json", true)]
    [InlineData("device/apps/v1/rejected.json", false)]
    public void FixturesMatchIndependentSchemaReader(string path, bool expectedValid)
    {
        _ = Registered.Value;
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", path)));
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var reference = "https://contracts.ringfour.dev/" + fixture.GetProperty("schema").GetString();
            var schema = JsonSchema.FromText(JsonSerializer.Serialize(new Dictionary<string, string> { ["$ref"] = reference }));
            var result = schema.Evaluate(fixture.GetProperty("value"), new EvaluationOptions {
                RequireFormatValidation = true, OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid == expectedValid,
                $"{fixture.GetProperty("name").GetString()}: expected valid={expectedValid}; {JsonSerializer.Serialize(result.Details)}");
        }
    }

    [Fact]
    public void ManifestSemanticFixturesMatchIndependentReader()
    {
        _ = Registered.Value;
        using var valid = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v1/manifest.json")));
        Assert.True(ManifestValid(valid.RootElement));
        using var rejected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v1/rejected.json")));
        foreach (var fixture in rejected.RootElement.EnumerateArray())
            Assert.False(ManifestValid(fixture.GetProperty("manifest")), fixture.GetProperty("name").GetString());
    }

    private static bool ManifestValid(JsonElement manifest)
    {
        var schema = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v1/manifest.schema.json\"}");
        if (!schema.Evaluate(manifest).IsValid || JsonSerializer.SerializeToUtf8Bytes(manifest).Length > 65536) return false;
        var targets = manifest.GetProperty("targets").EnumerateArray().ToArray();
        if (targets.Select(t => (t.GetProperty("os").GetString(), t.GetProperty("architecture").GetString())).Distinct().Count() != targets.Length) return false;
        static string Predicate(JsonElement p) => p.GetProperty("capability").GetString() + "|" +
            (p.TryGetProperty("instance_kind", out var kind) ? kind.GetString() : "");
        var caps = manifest.GetProperty("capabilities");
        var required = caps.GetProperty("required").EnumerateArray().Select(Predicate).ToHashSet();
        if (caps.GetProperty("optional").EnumerateArray().Select(Predicate).Any(required.Contains)) return false;
        var configuration = manifest.GetProperty("configuration");
        var configSchema = configuration.GetProperty("schema");
        var properties = configSchema.GetProperty("properties");
        if (configSchema.GetProperty("required").EnumerateArray().Any(n => !properties.TryGetProperty(n.GetString()!, out _))) return false;
        foreach (var property in properties.EnumerateObject())
        {
            var rule = property.Value;
            foreach (var (lower, upper) in new[] { ("minimum", "maximum"), ("minLength", "maxLength") })
                if (rule.TryGetProperty(lower, out var min) && rule.TryGetProperty(upper, out var max) && min.GetDecimal() > max.GetDecimal()) return false;
            if (rule.TryGetProperty("enum", out var choices))
            {
                var bounds = JsonNode.Parse(rule.GetRawText())!.AsObject();
                bounds.Remove("enum");
                var validator = JsonSchema.FromText(bounds.ToJsonString());
                if (choices.EnumerateArray().Any(choice => !validator.Evaluate(choice).IsValid)) return false;
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(configuration.GetProperty("defaults")).Length <= 16384
            && JsonSchema.FromText(configSchema.GetRawText()).Evaluate(configuration.GetProperty("defaults")).IsValid;
    }

    [Fact]
    public void ManifestCanonicalBytesMatchIndependentReader()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v1/manifest.json")));
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v1/canonical.json")));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, manifest.RootElement);
        var bytes = output.ToArray();
        Assert.Equal(golden.RootElement.GetProperty("json").GetString(), Encoding.UTF8.GetString(bytes));
        Assert.Equal(golden.RootElement.GetProperty("digest").GetString(), "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var item in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(item.Name); WriteCanonical(writer, item.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }
}
