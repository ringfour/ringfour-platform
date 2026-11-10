using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Robots;

// Shared caller-resolution + robot access checks for the robots and
// connectivity modules. The 404-for-non-member / 403-for-insufficient-role
// split matches IdentityAccess and is part of the authorization contract.
internal static class RobotAccess
{
    public static async Task<(User User, Robot Robot)> RequireRobotAccess(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid robotId,
        OrgRole minimumRole,
        CancellationToken cancellationToken)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        var robot = await db.Robots.SingleOrDefaultAsync(
            candidate => candidate.Id == robotId, cancellationToken);
        if (robot is null)
        {
            throw ApiException.NotFound(
                "The requested resource does not exist in this organization.");
        }
        await IdentityAccess.RequireMembership(
            db, robot.OrganizationId, user.Id, minimumRole, cancellationToken);
        return (user, robot);
    }
}
