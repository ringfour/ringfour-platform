using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Persistence;
using Ringfour.Api.Storage;

namespace Ringfour.Api.DevSessions;

public static class DevGrantRevocations
{
    public static async Task<Operation?> ScheduleAsync(RingfourDbContext db, Guid robotId, Guid? actorId,
        DateTimeOffset now, CancellationToken ct)
    {
        var grants = await db.DevLocalGrants
            .Where(item => item.Session.RobotId == robotId && item.ExpiresAt > now)
            .Select(item => item.Id)
            .ToListAsync(ct);
        if (grants.Count == 0) return null;

        await db.DevLocalGrants.Where(item => grants.Contains(item.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ExpiresAt, now), ct);
        var operationId = Guid.NewGuid();
        var operation = new Operation
        {
            Id = operationId,
            RobotId = robotId,
            Kind = "dev.session.grants.revoke",
            PayloadVersion = 1,
            Payload = JsonSerializer.Serialize(new { operation_id = operationId, grant_ids = grants }, RingfourJson.Options),
            State = OperationState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            DeadlineAt = now.AddYears(10),
            RequestedByUserId = actorId,
        };
        db.Operations.Add(operation);
        return operation;
    }
}
