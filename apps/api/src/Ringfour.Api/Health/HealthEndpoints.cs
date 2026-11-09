using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Health;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () =>
            Results.Ok(new LivenessResponse("ok", ServiceVersion.Value)));

        endpoints.MapGet("/health/ready", async (
            RingfourDbContext db,
            CancellationToken cancellationToken) =>
        {
            var postgresql = await db.Database.CanConnectAsync(cancellationToken)
                ? "ok"
                : "fail";
            var status = postgresql == "ok" ? "ok" : "fail";
            var response = new ReadinessResponse(
                status,
                ServiceVersion.Value,
                new Dictionary<string, string> { ["postgresql"] = postgresql });
            return Results.Json(
                response,
                statusCode: status == "ok"
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
        });
    }
}
