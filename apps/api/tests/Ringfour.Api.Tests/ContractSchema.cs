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

    // Isolate registrations from independent fixture/production validators.
    // Global schema IDs must not depend on which test class executes first.
    private static readonly ConcurrentDictionary<string, JsonSchema> Cache = new();

    public static void AssertValid(string schemaPath, string json)
    {
        var schema = Cache.GetOrAdd(schemaPath, path =>
            JsonSchema.FromText(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas", path)),
                new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
        using var document = JsonDocument.Parse(json);

        var results = schema.Evaluate(document.RootElement, Options);

        Assert.True(
            results.IsValid,
            $"{schemaPath} rejected the response: {json} " +
            $"(errors: {JsonSerializer.Serialize(results.Errors)}, " +
            $"details: {JsonSerializer.Serialize(results.Details)})");
    }
}
