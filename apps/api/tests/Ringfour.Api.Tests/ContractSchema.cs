using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;

namespace Ringfour.Api.Tests;

internal static class ContractSchema
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    // JsonSchema.Net registers each schema's $id globally on build; compile once per path.
    private static readonly ConcurrentDictionary<string, JsonSchema> Cache = new();

    public static void AssertValid(string schemaPath, string json)
    {
        var schema = Cache.GetOrAdd(schemaPath, path =>
            JsonSchema.FromText(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas", path))));
        using var document = JsonDocument.Parse(json);

        var results = schema.Evaluate(document.RootElement, Options);

        Assert.True(
            results.IsValid,
            $"{schemaPath} rejected the response: {JsonSerializer.Serialize(results.Errors)}");
    }
}
