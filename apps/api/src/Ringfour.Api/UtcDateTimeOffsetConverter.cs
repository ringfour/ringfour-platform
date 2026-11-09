using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ringfour.Api;

// Contracts require RFC3339 UTC instants with a Z suffix; DateTimeOffset's
// default JSON form carries a +00:00 offset instead.
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime);
}
