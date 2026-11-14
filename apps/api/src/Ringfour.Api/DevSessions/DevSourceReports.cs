using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.DevSessions;

public static class DevSourceReports
{
    private static readonly Regex Digest = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void MapDevSourceReports(this IEndpointRouteBuilder app) =>
        app.MapPost("/v1/device/dev-sessions/{sessionId:guid}/generations", Report);

    private static async Task<IResult> Report(HttpContext context, RingfourDbContext db, Guid sessionId, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, credential) = await DeviceAuth.VerifyCredential(db, credentialId, context.Request.Headers["x-ringfour-credential-secret"].ToString(), ct);
        JsonObject body;
        try { body = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid generation report."); }
        if (body.Count != 7 || body["operation_id"] is not JsonValue opValue || !opValue.TryGetValue<string>(out var opText) ||
            !Guid.TryParseExact(opText, "D", out var opId) || opId.ToString() != opText ||
            body["grant_id"] is not JsonValue grantValue || !grantValue.TryGetValue<string>(out var grantText) ||
            !Guid.TryParseExact(grantText, "D", out var grantId) || grantId.ToString() != grantText ||
            body["base_generation"] is not JsonValue baseValue || !baseValue.TryGetValue<int>(out var baseline) || baseline < 0 ||
            body["generation"] is not JsonValue numberValue || !numberValue.TryGetValue<int>(out var number) || (long)number != (long)baseline + 1 || number > 20 ||
            body["archive_size"] is not JsonValue sizeValue || !sizeValue.TryGetValue<int>(out var size) || size is < 1 or > 1048576 ||
            body["archive_sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out var hash) || !Digest.IsMatch(hash) ||
            body["lock_sha256"] is not JsonValue lockValue || !lockValue.TryGetValue<string>(out var lockHash) || !Digest.IsMatch(lockHash))
            throw ApiException.InvalidInput("Invalid generation report.");
        var session = await db.DevSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId && item.RobotId == robot.Id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist for this robot.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        await db.Entry(credential).ReloadAsync(ct);
        if (credential.RevokedAt is not null || (credential.ExpiresAt is not null && credential.ExpiresAt <= DateTimeOffset.UtcNow))
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        session = await db.DevSessions.SingleAsync(item => item.Id == sessionId, ct);
        var grant = await db.DevLocalGrants.Include(item => item.Operation).SingleOrDefaultAsync(item => item.Id == grantId && item.SessionId == sessionId, ct)
            ?? throw ApiException.NotFound("The local grant does not exist for this robot.");
        var previous = await db.DevSessionGenerations.Include(item => item.Operation).SingleOrDefaultAsync(item => item.OperationId == opId, ct);
        if (previous is not null)
        {
            var previousPayload = JsonNode.Parse(previous.Operation.Payload);
            if (previous.SessionId != sessionId || previous.Generation != number || previous.ArchiveSha256 != hash || previous.ArchiveSize != size ||
                previousPayload?["grant_id"]?.GetValue<string>() != grantId.ToString() || previousPayload?["lock_sha256"]?.GetValue<string>() != lockHash)
                throw ApiException.Conflict("conflict", "Generation operation was reused with different content.");
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        if (await db.Operations.AnyAsync(item => item.Id == opId, ct))
            throw ApiException.Conflict("conflict", "Generation operation ID is already in use.");
        if (grant.Operation.State != OperationState.Succeeded)
            throw ApiException.Conflict("grant_unavailable", "The local grant was not confirmed by the device.");
        if (session.Generation != baseline)
            throw ApiException.Conflict("stale_generation", "The base generation is stale.");
        var now = DateTimeOffset.UtcNow;
        var operation = new Operation { Id = opId, RobotId = robot.Id, Kind = "dev.session.sync", PayloadVersion = 1,
            Payload = body.ToJsonString(RingfourJson.Options), State = OperationState.Succeeded, CreatedAt = now, UpdatedAt = now,
            DeadlineAt = now.AddYears(1), RequestedByUserId = grant.Operation.RequestedByUserId, Result = "{}" };
        db.Operations.Add(operation);
        db.DevSessionGenerations.Add(new DevSessionGeneration { Id = Guid.NewGuid(), SessionId = sessionId, OperationId = opId,
            Generation = number, State = "committed", ArchiveSha256 = hash, ArchiveSize = size, CreatedAt = now });
        session.Generation = number;
        session.DependencyDigest = lockHash;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
