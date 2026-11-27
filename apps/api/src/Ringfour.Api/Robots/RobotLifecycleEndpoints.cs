using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Robots;

public static class RobotLifecycleEndpoints
{
    public static void MapRobotLifecycleEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/robots/{robotId:guid}/unenroll", Unenroll).RequireAuthorization();
        routes.MapDelete("/v1/robots/{robotId:guid}", Delete).RequireAuthorization();
        routes.MapPost("/v1/device/enrollment-state", EnrollmentState);
    }

    private static Task<IResult> Unenroll(HttpContext context, RingfourDbContext db, DeviceConnectionManager connections, Guid robotId, CancellationToken ct)
        => Release(context, db, connections, robotId, false, ct);
    private static Task<IResult> Delete(HttpContext context, RingfourDbContext db, DeviceConnectionManager connections, Guid robotId, CancellationToken ct)
        => Release(context, db, connections, robotId, true, ct);

    private static async Task<IResult> Release(HttpContext context, RingfourDbContext db, DeviceConnectionManager connections, Guid robotId, bool delete, CancellationToken ct)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, robotId, OrgRole.Owner, ct);
        var fence = connections.AppSessionFence(robotId);
        await fence.WaitAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robotId} FOR UPDATE", ct);
            await db.Entry(robot).ReloadAsync(ct);
            if (robot.DeletedAt is not null) throw ApiException.NotFound("Robot not found.");
            // Revalidate authority after waiting for lifecycle/membership mutations.
            await Identity.IdentityAccess.RequireMembership(db, robot.OrganizationId, user.Id, OrgRole.Owner, ct);
            if (await db.AppRuns.AnyAsync(r => r.RobotId == robotId && (r.State == "accepted" || r.State == "running"), ct) ||
                await db.RobotExecutionSlots.AnyAsync(slot => slot.RobotId == robotId, ct))
                throw ApiException.Conflict("robot_has_apps", "Stop active App runs and wait for execution cleanup before removing this robot's enrollment.");
            if (await db.AppOperations.AnyAsync(o => o.RobotId == robotId && (o.State == "queued" || o.State == "accepted" || o.State == "running"), ct))
                throw ApiException.Conflict("robot_has_apps", "Wait for pending App operations to finish before removing this robot's enrollment.");
            var now = DateTimeOffset.UtcNow;
            // Owner release detaches idle installations; it is not a device uninstall acknowledgement.
            // Keep configuration, settings, operations and run history for audit/provenance.
            await db.AppInstallations.Where(i => i.RobotId == robotId && i.RemovedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(i => i.InstalledBinding, (string?)null)
                    .SetProperty(i => i.Candidate, (string?)null)
                    .SetProperty(i => i.SelectedSettingsRevisionId, (Guid?)null)
                    .SetProperty(i => i.RemovedAt, now), ct);
            await db.AppDevelopmentSessions.Where(x => x.RobotId == robotId && x.ClosedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ClosedAt, now).SetProperty(x => x.GrantHash, (string?)null).SetProperty(x => x.GrantExpiresAt, (DateTimeOffset?)null), ct);
            robot.EnrolledAt = null;
            robot.ReleasedAt = now;
            if (delete) robot.DeletedAt = now;
            await db.RobotCredentials.Where(c => c.RobotId == robotId && c.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(c => c.RevokedAt, now), ct);
            await db.EnrollmentTokens.Where(t => t.RobotId == robotId && t.ConsumedAt == null && t.ExpiresAt > now)
                .ExecuteUpdateAsync(update => update.SetProperty(t => t.ExpiresAt, now), ct);
            await db.RobotAppInventories.Where(i => i.RobotId == robotId).ExecuteDeleteAsync(ct);
            db.RobotAuditEvents.Add(new RobotAuditEvent { Id = Guid.NewGuid(), RobotId = robotId, OccurredAt = now, ActorUserId = user.Id,
                Type = delete ? RobotAuditTypes.Deleted : RobotAuditTypes.Unenrolled });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            var session = connections.Get(robotId);
            if (session is not null)
            {
                try { session.Socket.Abort(); } catch (ObjectDisposedException) { }
                connections.Unregister(robotId, session.SessionId);
            }
            return Results.NoContent();
        }
        finally { fence.Release(); }
    }

    private static async Task<IResult> EnrollmentState(RingfourDbContext db, DeviceAuthenticateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Secret)) throw ApiException.InvalidInput("secret is required.");
        var credential = await db.RobotCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CredentialId, ct);
        if (credential is null || !DeviceAuth.FixedTimeEquals(DeviceAuth.HashSecret(request.Secret), credential.SecretHash))
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        var robot = await db.Robots.AsNoTracking().SingleAsync(r => r.Id == credential.RobotId, ct);
        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == robot.OrganizationId, ct);
        string? reason = robot.DeletedAt is not null ? "robot_deleted" : organization.DeletedAt is not null ? "organization_deleted" :
            robot.ReleasedAt is not null && credential.CreatedAt <= robot.ReleasedAt ? "robot_unenrolled" : null;
        return Results.Json(new { robot_id = robot.Id, credential_id = credential.Id, state = reason is null ? "enrolled" : "released", reason });
    }
}
