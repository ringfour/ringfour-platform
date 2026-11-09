namespace Ringfour.Api.Health;

public sealed record LivenessResponse(string Status, string Version);

public sealed record ReadinessResponse(
    string Status,
    string Version,
    IReadOnlyDictionary<string, string> Checks);
