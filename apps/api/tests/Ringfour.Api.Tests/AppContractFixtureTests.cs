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
            "apps/v1/manifest.schema.json", "apps/v2/manifest.schema.json", "apps/v2/authoring.schema.json", "apps/v2/settings.schema.json", "artifacts/v3/app-artifact-page.schema.json", "errors/v2/app-error.schema.json",
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

    [Fact]
    public void V2ManifestFixturesMatchIndependentReader()
    {
        _ = Registered.Value;
        using var valid = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v2/manifest.json")));
        Assert.True(ManifestValid(valid.RootElement));
        using var rejected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v2/rejected.json")));
        foreach (var fixture in rejected.RootElement.EnumerateArray())
            Assert.False(ManifestValid(fixture.GetProperty("manifest")), fixture.GetProperty("name").GetString());
    }

    private static bool ManifestValid(JsonElement manifest)
    {
        var version = manifest.TryGetProperty("schema_version", out var schemaVersion) ? schemaVersion.GetInt32() : 0;
        if (version is not (1 or 2)) return false;
        var schema = JsonSchema.FromText($"{{\"$ref\":\"https://contracts.ringfour.dev/apps/v{version}/manifest.schema.json\"}}");
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
        if (JsonSerializer.SerializeToUtf8Bytes(configuration.GetProperty("defaults")).Length > 16384
            || !JsonSchema.FromText(configSchema.GetRawText()).Evaluate(configuration.GetProperty("defaults")).IsValid) return false;
        if (version == 1) return true;
        var reserved = new HashSet<string>(StringComparer.Ordinal) { "PATH", "IFS", "ENV", "BASH_ENV", "BASHOPTS", "SHELLOPTS", "NODE_OPTIONS", "NODE_PATH", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "RUBYOPT", "RUBYLIB", "PERL5OPT", "PERL5LIB" };
        if (configuration.TryGetProperty("environment", out var environment))
        {
            var mappings = environment.EnumerateObject().ToArray();
            var names = properties.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            if (mappings.Length > 32 || mappings.Any(pair =>
                !System.Text.RegularExpressions.Regex.IsMatch(pair.Name, "^[A-Z_][A-Z0-9_]*$") ||
                pair.Name.StartsWith("RINGFOUR_", StringComparison.Ordinal) ||
                pair.Name.StartsWith("LD_", StringComparison.Ordinal) ||
                pair.Name.StartsWith("DYLD_", StringComparison.Ordinal) ||
                pair.Name.StartsWith("PYTHON", StringComparison.Ordinal) ||
                reserved.Contains(pair.Name) || !names.Contains(pair.Value.GetString() ?? ""))) return false;
        }
        if (!manifest.TryGetProperty("runtime_settings", out var runtime)) return true;
        var runtimeDefaults = runtime.GetProperty("defaults");
        return JsonSerializer.SerializeToUtf8Bytes(runtimeDefaults).Length <= 16384
            && JsonSchema.FromText(runtime.GetProperty("schema").GetRawText()).Evaluate(runtimeDefaults).IsValid;
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

    [Fact]
    public void V2ManifestCanonicalBytesMatchIndependentReader()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v2/manifest.json")));
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures/apps/v2/canonical.json")));
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
