using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.DevSessions;

public static class DevRunStatusEndpoints
{
    public static void MapDevRunStatusEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPut("/v1/device/dev-sessions/{sessionId:guid}/runs/{executionId:guid}", Report);

    private static int Rank(string state) => state switch { "queued" => 0, "preparing" => 1, "running" => 2, _ => 3 };
    private static bool Terminal(string state) => state is "succeeded" or "failed" or "cancelled";

    private static async Task<IResult> Report(HttpContext context, RingfourDbContext db, Guid sessionId, Guid executionId, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, credential) = await DeviceAuth.VerifyCredential(db, credentialId, context.Request.Headers["x-ringfour-credential-secret"].ToString(), ct);
        var bytes = await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct);
        JsonObject? payload;
        string? state;
        int? exitCode;
        JsonObject? failure;
        try
        {
            payload = JsonNode.Parse(bytes) as JsonObject;
            if (payload is null || payload.Count != 3 || payload.Any(pair => pair.Key is not ("state" or "exit_code" or "error"))) throw new JsonException();
            state = payload["state"]?.GetValue<string>();
            exitCode = payload["exit_code"]?.GetValue<int>();
            failure = payload["error"] as JsonObject;
            if (payload["error"] is not null && failure is null) throw new JsonException();
            if (failure is not null && (failure.Count != 2 || failure.Any(pair => pair.Key is not ("code" or "message")) ||
                failure["code"]?.GetValue<string>() is not { Length: <= 100 } || failure["message"]?.GetValue<string>() is not { Length: <= 500 })) throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw ApiException.InvalidInput("Invalid development execution status."); }
        if (state is not ("queued" or "preparing" or "running" or "succeeded" or "failed" or "cancelled") ||
            (!Terminal(state) && (exitCode is not null || failure is not null)) ||
            (state == "succeeded" && (exitCode != 0 || failure is not null)) || (state == "failed" && failure is null))
            throw ApiException.InvalidInput("Invalid development execution status.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        await db.Entry(credential).ReloadAsync(ct);
        if (credential.RevokedAt is not null || (credential.ExpiresAt is not null && credential.ExpiresAt <= DateTimeOffset.UtcNow))
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        var run = await db.DevSessionRuns.SingleOrDefaultAsync(item => item.Id == executionId && item.SessionId == sessionId && item.RobotId == robot.Id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist for this robot.");
        if (Terminal(run.State))
        {
            if (run.State != state || run.ExitCode != exitCode || !JsonNode.DeepEquals(run.Error is null ? null : JsonNode.Parse(run.Error), failure))
                throw ApiException.Conflict("conflict", "Terminal execution state is immutable.");
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        if (Rank(state) < Rank(run.State)) throw ApiException.Conflict("conflict", "Execution state cannot move backwards.");
        run.State = state;
        run.ExitCode = exitCode;
        run.Error = failure?.ToJsonString(RingfourJson.Options);
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var session = await db.DevSessions.SingleAsync(item => item.Id == sessionId, ct);
        if (session.State is not ("closing" or "closed" or "expired" or "failed"))
        {
            var active = await db.DevSessionRuns.Where(item => item.SessionId == sessionId && (item.State == "queued" || item.State == "preparing" || item.State == "running")).ToListAsync(ct);
            session.State = active.Count == 0 ? "ready" : active.Any(item => item.Id == session.LastExecutionId && item.State != "running") ? "starting" : "running";
            if (run.Id == session.LastExecutionId && (state == "running" || state == "succeeded" || failure?["code"]?.GetValue<string>() == "process_failed"))
                session.ActiveGeneration = run.Generation;
            if (run.Id == session.LastExecutionId && state == "failed") session.LastError = run.Error;
            session.UpdatedAt = run.UpdatedAt;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
