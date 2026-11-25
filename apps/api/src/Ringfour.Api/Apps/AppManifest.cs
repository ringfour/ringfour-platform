using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Json.Schema;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Apps;

public static class AppManifest
{
    public static JsonObject Validate(string json)
    {
        var manifest = AppProtocol.Validate(json, "manifest");
        var targets = manifest["targets"]!.AsArray();
        if (targets.Select(t => $"{t!["os"]}/{t["architecture"]}").Distinct().Count() != targets.Count)
            throw ApiException.InvalidInput("Image target platforms must be unique.");
        var required = manifest["capabilities"]!["required"]!.AsArray().Select(x => AppProtocol.Digest(x!)).ToHashSet();
        if (manifest["capabilities"]!["optional"]!.AsArray().Any(x => required.Contains(AppProtocol.Digest(x!))))
            throw ApiException.InvalidInput("Required and optional capability predicates overlap.");
        var schema = manifest["configuration"]!["schema"]!.AsObject();
        var properties = schema["properties"]!.AsObject();
        if (schema["required"]!.AsArray().Any(x => !properties.ContainsKey(x!.GetValue<string>())))
            throw ApiException.InvalidInput("Required configuration key is not declared.");
        foreach (var property in properties)
        {
            var rule = property.Value!.AsObject();
            if ((rule["minimum"] is not null && rule["maximum"] is not null && AppProtocol.Integer(rule["minimum"]!) > AppProtocol.Integer(rule["maximum"]!)) ||
                (rule["minLength"] is not null && AppProtocol.Integer(rule["minLength"]!) > (rule["maxLength"] is null ? 4096 : AppProtocol.Integer(rule["maxLength"]!))))
                throw ApiException.InvalidInput("Configuration bounds are reversed.");
            if (rule["enum"] is JsonArray choices)
            {
                var bounds = rule.DeepClone().AsObject(); bounds.Remove("enum");
                foreach (var choice in choices) Evaluate(bounds, choice!);
            }
        }
        if (manifest["schema_version"]!.GetValue<int>() == 2)
        {
            var mappings = manifest["configuration"]!["environment"] as JsonObject ?? new JsonObject();
            if (mappings.Count > 32) throw ApiException.InvalidInput("Environment mapping exceeds 32 entries.");
            foreach (var (name, keyNode) in mappings)
            {
                var key = keyNode?.GetValue<string>();
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z_][A-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                    key is null || !properties.ContainsKey(key) || ReservedEnvironmentName(name))
                    throw ApiException.InvalidInput("Environment mapping contains an invalid, reserved, or undeclared entry.");
            }
            ValidateEnvironmentValues(manifest, manifest["configuration"]!["defaults"]!);
            if (manifest["runtime_settings"] is JsonObject runtimeSettings)
            {
                var runtimeSchema = runtimeSettings["schema"]!.AsObject();
                var runtimeProperties = runtimeSchema["properties"]!.AsObject();
                if (runtimeSchema["required"]!.AsArray().Any(x => !runtimeProperties.ContainsKey(x!.GetValue<string>())))
                    throw ApiException.InvalidInput("Required runtime setting key is not declared.");
                foreach (var property in runtimeProperties)
                {
                    var rule = property.Value!.AsObject();
                    if ((rule["minimum"] is not null && rule["maximum"] is not null && AppProtocol.Integer(rule["minimum"]!) > AppProtocol.Integer(rule["maximum"]!)) ||
                        (rule["minLength"] is not null && AppProtocol.Integer(rule["minLength"]!) > (rule["maxLength"] is null ? 4096 : AppProtocol.Integer(rule["maxLength"]!))))
                        throw ApiException.InvalidInput("Runtime settings bounds are reversed.");
                }
                var effectiveRuntime = EffectiveSchema(runtimeSchema);
                if (AppProtocol.Canonical(runtimeSettings["defaults"]!).Length > 16384)
                    throw ApiException.InvalidInput("Runtime settings exceed 16384 bytes.");
                Evaluate(effectiveRuntime, runtimeSettings["defaults"]!);
            }
        }
        var effective = EffectiveSchema(schema);
        var defaults = manifest["configuration"]!["defaults"]!;
        if (AppProtocol.Canonical(defaults).Length > 16384)
            throw ApiException.InvalidInput("Configuration exceeds 16384 bytes.");
        Evaluate(effective, defaults);
        return manifest;
    }

    internal static JsonObject EffectiveSchema(JsonObject schema)
    {
        var effective = schema.DeepClone().AsObject();
        foreach (var property in effective["properties"]!.AsObject())
        {
            var rule = property.Value!.AsObject();
            if (rule["type"]!.GetValue<string>() == "string") rule["maxLength"] ??= 4096;
            if (rule["type"]!.GetValue<string>() == "integer")
            { rule["minimum"] ??= -9007199254740991L; rule["maximum"] ??= 9007199254740991L; }
        }
        return effective;
    }

    public static JsonObject ValidateRuntimeSettings(JsonObject manifest, JsonNode values)
    {
        if (manifest["runtime_settings"] is not JsonObject settings)
            throw ApiException.Conflict("settings_unsupported", "This App release does not declare live settings.");
        if (AppProtocol.Canonical(values).Length > 16384)
            throw ApiException.InvalidInput("Runtime settings exceed 16384 bytes.");
        try { Evaluate(EffectiveSchema(settings["schema"]!.AsObject()), values); }
        catch (ApiException) { throw new ApiException(400, "invalid_input", "Runtime settings violate the App-declared schema."); }
        return values.DeepClone().AsObject();
    }

    private static bool ReservedEnvironmentName(string name) => name.StartsWith("RINGFOUR_", StringComparison.Ordinal) ||
        name == "PATH" || name.StartsWith("LD_", StringComparison.Ordinal) || name.StartsWith("DYLD_", StringComparison.Ordinal) ||
        name.StartsWith("PYTHON", StringComparison.Ordinal) || new[] { "IFS", "ENV", "BASH_ENV", "BASHOPTS", "SHELLOPTS", "NODE_OPTIONS", "NODE_PATH", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "RUBYOPT", "RUBYLIB", "PERL5OPT", "PERL5LIB" }.Contains(name, StringComparer.Ordinal);

    public static JsonObject Environment(JsonObject manifest, JsonNode configuration)
    {
        var output = new JsonObject();
        if (manifest["schema_version"]!.GetValue<int>() != 2 || manifest["configuration"]!["environment"] is not JsonObject mappings) return output;
        foreach (var (name, keyNode) in mappings)
        {
            var key = keyNode!.GetValue<string>();
            if (configuration[key] is not JsonNode value) continue;
            if (value.GetValueKind() == System.Text.Json.JsonValueKind.String && value.GetValue<string>().Contains('\0'))
                throw ApiException.InvalidInput("Mapped environment values cannot contain NUL.");
            output[name] = value.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.Number => AppProtocol.Integer(value).ToString(CultureInfo.InvariantCulture),
                _ => throw ApiException.InvalidInput("Environment values must be scalar.")
            };
        }
        var bytes = output.Sum(item => Encoding.UTF8.GetByteCount(item.Key) + Encoding.UTF8.GetByteCount(item.Value!.GetValue<string>()) + 1);
        if (bytes > 16384) throw ApiException.InvalidInput("Mapped environment exceeds 16384 UTF-8 bytes.");
        return output;
    }

    internal static void ValidateEnvironmentValues(JsonObject manifest, JsonNode configuration)
    {
        if (manifest["schema_version"]!.GetValue<int>() != 2 || manifest["configuration"]!["environment"] is not JsonObject mappings) return;
        _ = Environment(manifest, configuration);
    }

    internal static void Evaluate(JsonObject schema, JsonNode value)
    {
        var validator = JsonSchema.FromText(schema.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!validator.Evaluate(document.RootElement).IsValid)
            throw ApiException.InvalidInput("Configuration defaults or enum values violate their schema.");
    }
}
