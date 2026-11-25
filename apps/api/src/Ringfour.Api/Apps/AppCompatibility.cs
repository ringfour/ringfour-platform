using System.Globalization;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Apps;

public static class AppCompatibility
{
    public static JsonObject Evaluate(JsonObject manifest, JsonObject projection, DateTimeOffset now, int freshnessSeconds = 45)
    {
        var result = new JsonObject {
            ["status"] = "inventory_unavailable", ["checked_at"] = AppProtocol.Timestamp(now),
            ["inventory_observed_at"] = null, ["target"] = null,
            ["missing_required"] = new JsonArray(), ["missing_optional"] = new JsonArray(), ["missing_native"] = new JsonArray(),
        };
        if (projection["connectivity"]?.GetValue<string>() != "online") return result;
        if (projection["support"]?.GetValue<string>() != "supported")
        { result["status"] = "daemon_upgrade_required"; return result; }
        if (manifest["schema_version"]!.GetValue<int>() == 2 && projection["supports_manifest_v2"]?.GetValue<bool>() != true)
        { result["status"] = "daemon_upgrade_required"; return result; }
        if (projection["inventory"] is not JsonObject inventory || inventory["state"]!.GetValue<string>() != "available" ||
            projection["received_at"] is null ||
            DateTimeOffset.Parse(projection["received_at"]!.GetValue<string>(), CultureInfo.InvariantCulture) < now.AddSeconds(-freshnessSeconds))
            return result;
        var observation = inventory["current"]!.AsObject();
        result["inventory_observed_at"] = observation["observed_at"]!.DeepClone();
        var platform = projection["platform"]!.AsObject();
        var target = manifest["targets"]!.AsArray().FirstOrDefault(t =>
            JsonNode.DeepEquals(t!["os"], platform["os"]) && JsonNode.DeepEquals(t["architecture"], platform["architecture"]));
        if (target is null) { result["status"] = "no_matching_image"; return result; }
        result["target"] = target.DeepClone();
        var capabilities = observation["capabilities"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
        bool Present(JsonNode predicate) => capabilities.Contains(predicate["capability"]!.GetValue<string>()) &&
            (predicate["instance_kind"] is null || observation["instances"]?["arms"] is JsonArray arms && arms.Count > 0);
        foreach (var (source, output) in new[] { ("required", "missing_required"), ("optional", "missing_optional") })
            foreach (var predicate in manifest["capabilities"]![source]!.AsArray())
                if (!Present(predicate!)) result[output]!.AsArray().Add(predicate!.DeepClone());
        foreach (var fact in target["native_requirements"]!.AsArray())
            if (!platform["native_facts"]!.AsArray().Any(x => JsonNode.DeepEquals(x, fact)))
                result["missing_native"]!.AsArray().Add(fact!.DeepClone());
        result["status"] = result["missing_required"]!.AsArray().Count > 0 ? "missing_required" :
            result["missing_native"]!.AsArray().Count > 0 ? "native_requirement_unavailable" : "compatible";
        return result;
    }
}
