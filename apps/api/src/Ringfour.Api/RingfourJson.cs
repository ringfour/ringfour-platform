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
}
