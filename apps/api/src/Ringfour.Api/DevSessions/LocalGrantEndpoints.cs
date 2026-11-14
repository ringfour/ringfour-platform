using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Missions;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.DevSessions;

public static class LocalGrantEndpoints
{
    private const string GrantRoute = "POST /v1/dev-sessions/{session_id}/local-grants";
    private static readonly Regex Digest = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void MapLocalGrantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/v1/device/local-dev-endpoint", PublishEndpoint);
        endpoints.MapPost("/v1/dev-sessions/{sessionId:guid}/local-grants", Grant).RequireAuthorization();
    }

    private static async Task<JsonObject> ReadBody(HttpContext context, CancellationToken ct)
    {
        try { return JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid local development request."); }
    }

    private static bool ValidUrl(string? value)
    {
        if (value is null) return true;
        return value.Length <= 512 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";
    }

    private static async Task<IResult> PublishEndpoint(HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, credential) = await DeviceAuth.VerifyCredential(db, credentialId,
            context.Request.Headers["x-ringfour-credential-secret"].ToString(), ct);
        var body = await ReadBody(context, ct);
        if (body.Count != 2 || body["certificate_sha256"] is not JsonValue hashValue ||
            !hashValue.TryGetValue<string>(out var hash) || !Digest.IsMatch(hash) || !body.ContainsKey("daemon_url"))
            throw ApiException.InvalidInput("Invalid local development endpoint.");
        string? url = null;
        if (body["daemon_url"] is not null && (body["daemon_url"] is not JsonValue urlValue || !urlValue.TryGetValue<string>(out url)))
            throw ApiException.InvalidInput("Invalid local development endpoint.");
        if (!ValidUrl(url)) throw ApiException.InvalidInput("Invalid local development endpoint.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        // Recheck after locking so a credential revoked while waiting cannot advertise.
        await db.Entry(credential).ReloadAsync(ct);
        if (credential.RevokedAt is not null || (credential.ExpiresAt is not null && credential.ExpiresAt <= DateTimeOffset.UtcNow))
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        var item = await db.DevLocalEndpoints.SingleOrDefaultAsync(item => item.RobotId == robot.Id, ct);
        if (item is null) { item = new DevLocalEndpoint { RobotId = robot.Id, CredentialId = credential.Id, CertificateSha256 = hash }; db.DevLocalEndpoints.Add(item); }
        item.CredentialId = credential.Id;
        item.CertificateSha256 = hash;
        item.DaemonUrl = url;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Grant(HttpContext context, RingfourDbContext db, DeviceConnectionManager manager,
        IConfiguration configuration, Guid sessionId, CancellationToken ct)
    {
        var session = await db.DevSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var (user, robot) = await RobotAccess.RequireRobotAccess(context, db, session.RobotId, OrgRole.Developer, ct);
        var member = await IdentityAccess.RequireMembership(db, robot.OrganizationId, user.Id, OrgRole.Developer, ct);
        if (session.OwnerUserId != user.Id && member.Role != OrgRole.Owner)
            throw ApiException.Forbidden("Only the session owner or an organization owner may create a local grant.");
        var key = DeploymentEndpoints.Key(context);
        var body = await ReadBody(context, ct);
        if (body.Count != 2 || body["grant_id"] is not JsonValue grantValue || !grantValue.TryGetValue<string>(out var grantText) ||
            !Guid.TryParseExact(grantText, "D", out var grantId) || grantId.ToString() != grantText ||
            body["token_sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out var hash) || !Digest.IsMatch(hash))
            throw ApiException.InvalidInput("Invalid local grant request.");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{grantId}:{hash}")));
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        session = await db.DevSessions.SingleAsync(item => item.Id == sessionId, ct);
        var previous = await db.OperationIdempotencyRecords.SingleOrDefaultAsync(item => item.PrincipalUserId == user.Id &&
            item.Route == GrantRoute && item.Target == sessionId.ToString() && item.Key == key, ct);
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
            var replay = await db.DevLocalGrants.SingleAsync(item => item.OperationId == previous.OutcomeOperationId, ct);
            await tx.CommitAsync(ct);
            return Results.Json(Response(replay), RingfourJson.Options, statusCode: 202);
        }
        if (previous is not null) db.OperationIdempotencyRecords.Remove(previous);
        if (session.ExpiresAt <= now || session.State is not ("ready" or "running" or "syncing" or "starting" or "stopping"))
            throw ApiException.Conflict("session_unavailable", "The development session is not open.");
        var presence = await db.RobotPresences.SingleOrDefaultAsync(item => item.RobotId == robot.Id, ct);
        if (manager.Get(robot.Id) is null || !OperationEndpoints.ComputeOnline(presence?.LastSeenAt, now, configuration.GetValue("Connectivity:OfflineThresholdSeconds", 45)))
            throw new ApiException(503, "robot_unavailable", "The robot is offline.");
        var endpoint = await db.DevLocalEndpoints.Include(item => item.Credential).SingleOrDefaultAsync(item => item.RobotId == robot.Id, ct);
        if (endpoint is null || endpoint.Credential.RevokedAt is not null || (endpoint.Credential.ExpiresAt is not null && endpoint.Credential.ExpiresAt <= now))
            throw new ApiException(503, "local_endpoint_unavailable", "The robot has no current local endpoint.");
        if (await db.DevLocalGrants.AnyAsync(item => item.Id == grantId, ct))
            throw ApiException.Conflict("conflict", "The local grant ID is already in use.");
        var expires = session.ExpiresAt < now.AddMinutes(5) ? session.ExpiresAt : now.AddMinutes(5);
        var operationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { operation_id = operationId, session_id = sessionId, grant_id = grantId,
            token_sha256 = hash, expires_at = RingfourJson.UtcTimestamp(expires), scopes = new[] { "source", "output" } }, RingfourJson.Options);
        db.Operations.Add(new Operation { Id = operationId, RobotId = robot.Id, Kind = "dev.session.grant", PayloadVersion = 1,
            Payload = payload, State = OperationState.Queued, CreatedAt = now, UpdatedAt = now, DeadlineAt = expires, RequestedByUserId = user.Id });
        var grant = new DevLocalGrant { Id = grantId, SessionId = sessionId, OperationId = operationId,
            CertificateSha256 = endpoint.CertificateSha256, DaemonUrl = endpoint.DaemonUrl, ExpiresAt = expires };
        db.DevLocalGrants.Add(grant);
        db.OperationIdempotencyRecords.Add(new OperationIdempotencyRecord { Id = Guid.NewGuid(), PrincipalUserId = user.Id,
            Route = GrantRoute, Target = sessionId.ToString(), Key = key, RequestFingerprint = fingerprint, OutcomeOperationId = operationId,
            CreatedAt = now, ExpiresAt = now.AddHours(configuration.GetValue("Idempotency:TtlHours", 24)) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await OperationEndpoints.DispatchAsync(db, manager, robot.Id, operationId, ct);
        return Results.Json(Response(grant), RingfourJson.Options, statusCode: 202);
    }

    private static object Response(DevLocalGrant grant) => new { grant_id = grant.Id, operation_id = grant.OperationId,
        expires_at = grant.ExpiresAt, daemon_url = grant.DaemonUrl, certificate_sha256 = grant.CertificateSha256 };
}
