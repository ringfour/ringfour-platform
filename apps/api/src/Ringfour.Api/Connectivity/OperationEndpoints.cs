using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Connectivity;

public static class OperationEndpoints
{
    private const string CreateOperationRoute = "POST /v1/robots/{robot_id}/operations";

    public static void MapOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();

        group.MapGet("/robots/{robotId:guid}/status", GetStatus);
    }

    // Online is derived from server receive time, never from what the device
    // claims; a connected robot that stops sending goes offline purely by the
    // freshness window.
    public static bool ComputeOnline(DateTimeOffset? lastSeenAt, DateTimeOffset now, int offlineThresholdSeconds) =>
        lastSeenAt is not null && now - lastSeenAt.Value < TimeSpan.FromSeconds(offlineThresholdSeconds);

    private static async Task<RobotStatusResponse> GetStatus(
        HttpContext httpContext,
        RingfourDbContext db,
        IConfiguration configuration,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (_, robot) = await RobotAccess.RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Operator, cancellationToken);
        var threshold = configuration.GetValue("Connectivity:OfflineThresholdSeconds", 45);
        var presence = await db.RobotPresences.SingleOrDefaultAsync(
            candidate => candidate.RobotId == robot.Id, cancellationToken);
        return new RobotStatusResponse(
            robot.Id,
            ComputeOnline(presence?.LastSeenAt, DateTimeOffset.UtcNow, threshold),
            presence?.LastSeenAt,
            presence?.DaemonVersion,
            presence?.ProtocolVersion);
    }

}
