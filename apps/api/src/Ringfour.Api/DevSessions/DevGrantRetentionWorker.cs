using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.DevSessions;

// A grant stays available for seven days after expiry so an offline daemon can
// deliver its generation report. Active API idempotency records extend that
// retention when configured for longer than the delivery window.
public sealed class DevGrantRetentionWorker(IServiceScopeFactory scopes, ILogger<DevGrantRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ReportGrace = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
                var now = DateTimeOffset.UtcNow;
                var expired = await db.DevLocalGrants
                    .Where(grant => grant.ExpiresAt < now - ReportGrace &&
                        !db.OperationIdempotencyRecords.Any(record => record.OutcomeOperationId == grant.OperationId && record.ExpiresAt > now))
                    .OrderBy(grant => grant.ExpiresAt)
                    .Take(200)
                    .ToListAsync(stoppingToken);
                if (expired.Count > 0)
                {
                    db.DevLocalGrants.RemoveRange(expired);
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "development grant retention cleanup failed"); }

            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
