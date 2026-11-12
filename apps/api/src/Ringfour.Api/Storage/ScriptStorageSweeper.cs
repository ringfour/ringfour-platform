using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Storage;

/// <summary>Retries cleanup of expired, unfinalized uploads using durable database state.</summary>
public sealed class ScriptStorageSweeper(IServiceScopeFactory scopeFactory, IConfiguration configuration,
    ILogger<ScriptStorageSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(configuration["Storage:Backend"]))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await SweepOnceAsync(scope.ServiceProvider.GetRequiredService<RingfourDbContext>(),
                        scope.ServiceProvider.GetRequiredService<IObjectStore>(), DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Script upload cleanup failed; it will retry"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public static async Task SweepOnceAsync(RingfourDbContext db, IObjectStore store, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddHours(-24);
        var candidates = await db.ScriptRevisions.AsNoTracking().Where(item =>
                (item.State == "uploading" && item.UploadExpiresAt < cutoff) ||
                (item.State == "ready" && item.UploadCleanedAt == null))
            .OrderBy(item => item.UploadExpiresAt).Select(item => item.Id).Take(100).ToListAsync(ct);
        foreach (var id in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM script_revisions WHERE id = {id} FOR UPDATE", ct);
            var revision = await db.ScriptRevisions.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (revision is not null && revision.State == "uploading" && revision.UploadExpiresAt < cutoff)
            {
                // A crash before commit merely retries this idempotent delete.
                await store.DeleteAsync($"incoming/{id:N}", ct);
                db.ScriptRevisions.Remove(revision);
                await db.SaveChangesAsync(ct);
            }
            else if (revision is not null && revision.State == "ready" && revision.UploadCleanedAt is null)
            {
                await store.DeleteAsync($"incoming/{id:N}", ct);
                revision.UploadCleanedAt = now;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
    }
}
