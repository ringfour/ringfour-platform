using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Persistence;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Health;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () =>
            Results.Ok(new LivenessResponse("ok", ServiceVersion.Value)));

        endpoints.MapGet("/health/ready", async (
            RingfourDbContext db,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            var postgresql = await db.Database.CanConnectAsync(cancellationToken)
                ? "ok"
                : "fail";
            var storage = "fail";
            try
            {
                await services.GetRequiredService<IObjectStore>().CheckReadyAsync(cancellationToken);
                storage = "ok";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* Do not expose backend configuration or credentials. */ }
            var status = postgresql == "ok" && storage == "ok" ? "ok" : "fail";
            var response = new ReadinessResponse(
                status,
                ServiceVersion.Value,
                new Dictionary<string, string> { ["postgresql"] = postgresql, ["storage"] = storage });
            return Results.Json(
                response,
                statusCode: status == "ok"
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
        });
    }
}
