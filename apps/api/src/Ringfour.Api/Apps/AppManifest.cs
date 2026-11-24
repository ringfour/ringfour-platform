using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var effective = schema.DeepClone().AsObject();
        foreach (var property in effective["properties"]!.AsObject())
        {
            var rule = property.Value!.AsObject();
            if (rule["type"]!.GetValue<string>() == "string") rule["maxLength"] ??= 4096;
            if (rule["type"]!.GetValue<string>() == "integer")
            { rule["minimum"] ??= -9007199254740991L; rule["maximum"] ??= 9007199254740991L; }
        }
        var defaults = manifest["configuration"]!["defaults"]!;
        if (AppProtocol.Canonical(defaults).Length > 16384)
            throw ApiException.InvalidInput("Configuration exceeds 16384 bytes.");
        Evaluate(effective, defaults);
        return manifest;
    }

    internal static void Evaluate(JsonObject schema, JsonNode value)
    {
        var validator = JsonSchema.FromText(schema.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!validator.Evaluate(document.RootElement).IsValid)
            throw ApiException.InvalidInput("Configuration defaults or enum values violate their schema.");
    }
}
