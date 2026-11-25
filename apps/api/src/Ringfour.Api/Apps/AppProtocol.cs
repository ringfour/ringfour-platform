using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Apps;

public static class AppProtocol
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Schemas = new(() =>
    {
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        foreach (var file in new[] { "apps/v1/manifest.schema.json", "apps/v2/manifest.schema.json", "apps/v2/authoring.schema.json", "apps/v2/settings.schema.json", "artifacts/v3/app-artifact-page.schema.json", "errors/v2/app-error.schema.json", "apps/v1/api.schema.json", "apps/v1/requests.schema.json", "device/apps/v1/protocol.schema.json" })
            JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/schemas", file)), options);
        var schemas = new[] { "negotiation_request", "inventory_report", "robot_app_status", "compatibility", "observations", "apply_command", "start_command", "stop_command" }.ToDictionary(
            definition => definition,
            definition => JsonSchema.FromText(JsonSerializer.Serialize(new Dictionary<string, string> {
                ["$ref"] = "https://contracts.ringfour.dev/device/apps/v1/protocol.schema.json#/$defs/" + definition }), options));
        foreach (var definition in new[] { "publish", "share", "import", "registry_grant", "install", "configure", "update", "rollback", "start", "stop" })
            schemas["request_" + definition] = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v1/requests.schema.json#/$defs/" + definition + "\"}", options);
        schemas["settings_write"] = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v2/settings.schema.json#/$defs/settings_write\"}", options);
        foreach (var definition in new[] { "revision", "application", "settings_state", "saved_for_next_start", "pending_application" })
            schemas["settings_" + definition] = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v2/settings.schema.json#/$defs/" + definition + "\"}", options);
        schemas["settings_apply_command"] = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/device/apps/v1/protocol.schema.json#/$defs/settings_apply_command\"}", options);
        schemas["manifest"] = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v1/manifest.schema.json\"}", options);
        schemas["manifest_v2"] = JsonSchema.FromText("{\"$ref\":\"https://contracts.ringfour.dev/apps/v2/manifest.schema.json\"}", options);
        return schemas;
    });

    public static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    public static long Integer(JsonNode value) => decimal.ToInt64(decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture));

    public static JsonObject Validate(string json, string definition)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > 65536) throw new ApiException(413, "invalid_input", "App protocol body exceeds 65536 bytes.");
            using var document = JsonDocument.Parse(json);
            Check(document.RootElement);
            var manifestVersion = document.RootElement.TryGetProperty("schema_version", out var versionElement) && versionElement.TryGetInt32(out var parsedVersion) ? parsedVersion : 1;
            var schema = definition == "manifest"
                ? Schemas.Value[manifestVersion == 2 ? "manifest_v2" : "manifest"]
                : Schemas.Value[definition];
            if (!schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true }).IsValid)
                throw ApiException.InvalidInput("Invalid App protocol body.");
            var value = JsonNode.Parse(json)!.AsObject();
            if (definition == "inventory_report")
            {
                var inventory = value["inventory"]!.AsObject();
                foreach (var observation in new[] { inventory["current"], inventory["last_successful"] })
                {
                    if (observation is null) continue;
                    var capabilities = observation["capabilities"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
                    var instances = observation["instances"]!.AsObject();
                    if (instances["arms"] is JsonArray arms && arms.Count > 0 &&
                        (!capabilities.Contains("groups") || instances["groups"] is not JsonArray groups ||
                         arms.Any(arm => !groups.Any(group => JsonNode.DeepEquals(arm, group)))))
                        throw ApiException.InvalidInput("Arm instances must be named groups with the groups capability.");
                }
            }
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw ApiException.InvalidInput("Invalid App protocol JSON.");
        }
    }

    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw ApiException.InvalidInput("Duplicate JSON member.");
                Check(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Check(item);
        else if (value.ValueKind == JsonValueKind.String)
            _ = value.GetString(); // Reject malformed Unicode escapes.
    }

    public static async Task<JsonObject> Read(HttpRequest request, string definition, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > 65536) throw new ApiException(413, "invalid_input", "App protocol body exceeds 65536 bytes.");
            output.Write(buffer, 0, count);
        }
        try { return Validate(new UTF8Encoding(false, true).GetString(output.ToArray()), definition); }
        catch (DecoderFallbackException) { throw ApiException.InvalidInput("Invalid UTF-8."); }
    }

    public static string Digest(JsonNode value) => Convert.ToHexStringLower(SHA256.HashData(Canonical(value)));

    public static byte[] Canonical(JsonNode value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(writer, value);
        }
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var field in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
            { writer.WritePropertyName(field.Key); Write(writer, field.Value); }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        { writer.WriteStartArray(); foreach (var item in array) Write(writer, item); writer.WriteEndArray(); }
        else if (node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number)
            writer.WriteNumberValue(Integer(scalar));
        else if (node is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            // Utf8JsonWriter's encoder escapes supplementary Unicode even in
            // relaxed mode; v1 canonical JSON retains valid UTF-8 scalars.
            var quoted = new StringBuilder("\"");
            foreach (var character in text.GetValue<string>())
                quoted.Append(character switch {
                    '\"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                    '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                    _ => character.ToString()
                });
            quoted.Append('\"');
            writer.WriteRawValue(quoted.ToString());
        }
        else if (node is null) writer.WriteNullValue();
        else node.WriteTo(writer);
    }
}
