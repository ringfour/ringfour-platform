using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ringfour.Api;

// Shared JSON conventions: snake_case properties and lowercase enum strings,
// matching the contracts in packages/contracts.
public static class RingfourJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
            new UtcDateTimeOffsetConverter(),
        },
    };

    // Contracts require RFC3339 UTC instants with a Z suffix. DOM payloads
    // (JsonObject/JsonNode) ignore serializer converters, so timestamps built
    // into envelopes must be pre-formatted with this helper.
    public static string UtcTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
