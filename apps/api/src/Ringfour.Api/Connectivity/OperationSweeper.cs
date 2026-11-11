using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Connectivity;

// Durable background processing for operations, per architecture: state is
// PostgreSQL, this service just sweeps it. Two duties: fail nonterminal
// operations whose deadline passed, and redeliver queued operations to live
// sessions (at-least-once; the daemon deduplicates).
public sealed class OperationSweeper(
    IServiceScopeFactory scopeFactory,
    DeviceConnectionManager manager,
    IConfiguration configuration,
    ConnectivityMetrics metrics,
    ILogger<OperationSweeper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Connectivity:SweepIntervalSeconds", 2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
                var now = DateTimeOffset.UtcNow;
                await SweepOnceAsync(db, manager, now, stoppingToken, metrics);
                var queued = db.Operations.Where(operation => operation.State == OperationState.Queued);
                var count = await queued.LongCountAsync(stoppingToken);
                var oldest = await queued.Select(operation => (DateTimeOffset?)operation.CreatedAt).MinAsync(stoppingToken);
                metrics.Queue(count, oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "operation sweep failed");
            }
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public static async Task SweepOnceAsync(
        RingfourDbContext db,
        DeviceConnectionManager manager,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        ConnectivityMetrics? metrics = null)
    {
        var expired = await db.Operations
            .Where(operation =>
                (operation.State == OperationState.Queued ||
                 operation.State == OperationState.Accepted ||
                 operation.State == OperationState.Running) &&
                operation.DeadlineAt < now)
            .ToListAsync(cancellationToken);
        foreach (var operation in expired)
        {
            operation.State = OperationState.Failed;
            operation.UpdatedAt = now;
            operation.Result = """{"error":{"code":"deadline_exceeded","message":"The operation deadline passed before completion."}}""";
        }
        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var _ in expired) metrics?.Terminal("failed");
        }

        var connectedRobotIds = manager.All().Select(session => session.RobotId).ToList();
        if (connectedRobotIds.Count == 0)
        {
            return;
        }
        var queued = await db.Operations
            .Where(operation =>
                connectedRobotIds.Contains(operation.RobotId) &&
                operation.State == OperationState.Queued)
            .OrderBy(operation => operation.CreatedAt)
            .ThenBy(operation => operation.Id)
            .ToListAsync(cancellationToken);
        foreach (var operation in queued)
        {
            await OperationEndpoints.DispatchAsync(
                db, manager, operation.RobotId, operation.Id, cancellationToken);
        }
    }
}
