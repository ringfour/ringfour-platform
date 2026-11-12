using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Robots;

public static partial class PairingEndpoints
{
    private const int LifetimeMinutes = 10;
    private const int MaxPending = 10000;
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();
    [GeneratedRegex("^[A-Z2-7]{26}$")]
    private static partial Regex CodePattern();
    [GeneratedRegex("^rfs_[A-Za-z0-9_-]{43}$")]
    private static partial Regex SecretPattern();

    public sealed record StartRequest(Guid RequestId, string? CodeHash, string? CredentialHash);
    public sealed record StartResponse(Guid RequestId, DateTimeOffset ExpiresAt);
    public sealed record PollRequest(string? CredentialSecret);
    public sealed record PollResponse(string State, Guid? RobotId, Guid? OrganizationId, Guid? CredentialId);
    public sealed record ClaimRequest(string? Code, Guid? RobotId, string? Name);

    public static void MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/device/pairing-requests", Start).RequireRateLimiting("pairing");
        app.MapPost("/v1/device/pairing-requests/{requestId:guid}/poll", Poll).RequireRateLimiting("pairing");
        app.MapPost("/v1/organizations/{orgId:guid}/pairing-claims", Claim)
            .RequireAuthorization().RequireRateLimiting("pairing");
    }

    private static async Task<IResult> Start(StartRequest body, RingfourDbContext db, CancellationToken ct)
    {
        if (body.RequestId == Guid.Empty || body.CodeHash is null || body.CredentialHash is null ||
            !HashPattern().IsMatch(body.CodeHash) || !HashPattern().IsMatch(body.CredentialHash))
            throw ApiException.InvalidInput("Invalid pairing request.");
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48270931)", ct);
        var existing = await db.DevicePairingRequests.SingleOrDefaultAsync(x => x.Id == body.RequestId, ct);
        if (existing is not null)
        {
            if (existing.CodeHash != body.CodeHash || existing.CredentialHash != body.CredentialHash)
                throw ApiException.Conflict("conflict", "Pairing request ID already used.");
            await tx.CommitAsync(ct);
            return Results.Ok(new StartResponse(existing.Id, existing.ExpiresAt));
        }
        await db.DevicePairingRequests.Where(x => x.ClaimedAt == null && x.ExpiresAt < now.AddHours(-24)).ExecuteDeleteAsync(ct);
        if (await db.DevicePairingRequests.CountAsync(x => x.ClaimedAt == null && x.ExpiresAt > now, ct) >= MaxPending)
            throw new ApiException(503, "capacity_exceeded", "Pairing is temporarily unavailable.");
        if (await db.DevicePairingRequests.AnyAsync(x => x.CodeHash == body.CodeHash || x.CredentialHash == body.CredentialHash, ct) ||
            await db.RobotCredentials.AnyAsync(x => x.SecretHash == body.CredentialHash, ct))
            throw ApiException.Conflict("conflict", "Pairing request conflicts with an existing request.");
        var pairing = new DevicePairingRequest
        {
            Id = body.RequestId,
            CodeHash = body.CodeHash,
            CredentialHash = body.CredentialHash,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(LifetimeMinutes)
        };
        db.DevicePairingRequests.Add(pairing);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new StartResponse(pairing.Id, pairing.ExpiresAt));
    }

    private static async Task<IResult> Poll(Guid requestId, PollRequest body, RingfourDbContext db, CancellationToken ct)
    {
        if (body.CredentialSecret is null || !SecretPattern().IsMatch(body.CredentialSecret))
            throw ApiException.InvalidToken("Invalid pairing proof.");
        var pair = await db.DevicePairingRequests.Include(x => x.Robot).Include(x => x.Credential)
            .SingleOrDefaultAsync(x => x.Id == requestId, ct);
        var hash = DeviceAuth.HashSecret(body.CredentialSecret);
        if (pair is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(pair.CredentialHash)))
            throw ApiException.InvalidToken("Invalid pairing proof.");
        if (pair.ClaimedAt is not null)
        {
            if (pair.Credential!.RevokedAt is not null || pair.Credential.ExpiresAt <= DateTimeOffset.UtcNow)
                throw ApiException.InvalidToken("Pairing credential is no longer valid.");
            return Results.Ok(new PollResponse("claimed", pair.RobotId, pair.Robot!.OrganizationId, pair.CredentialId));
        }
        if (pair.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ApiException(410, "pairing_expired", "Pairing request expired.");
        return Results.Ok(new PollResponse("pending", null, null, null));
    }

    private static async Task<IResult> Claim(Guid orgId, ClaimRequest body, HttpContext http, RingfourDbContext db, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(http), ct);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Owner, ct);
        var code = body.Code?.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (code is null || !CodePattern().IsMatch(code) || (body.RobotId is null) == (body.Name is null) ||
            (body.Name is not null && (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200)))
            throw ApiException.InvalidInput("Provide a valid pairing code and exactly one robot ID or name.");
        var hash = DeviceAuth.HashSecret(code);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pair = await db.DevicePairingRequests.FromSqlInterpolated($"SELECT * FROM device_pairing_requests WHERE code_hash = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (pair is null || (pair.ClaimedAt is null && pair.ExpiresAt <= DateTimeOffset.UtcNow))
            throw ApiException.NotFound("Pairing code is invalid or expired.");
        if (pair.ClaimedAt is not null)
            throw ApiException.Conflict("conflict", "Pairing code has already been claimed.");
        var now = DateTimeOffset.UtcNow;
        Robot robot;
        if (body.RobotId is not null)
        {
            robot = await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {body.RobotId.Value} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw ApiException.NotFound("Robot not found.");
            if (robot.OrganizationId != orgId) throw ApiException.NotFound("Robot not found.");
            if (robot.EnrolledAt is not null) throw ApiException.Conflict("conflict", "Robot is already enrolled.");
        }
        else
        {
            robot = new Robot { Id = Guid.NewGuid(), OrganizationId = orgId, Name = body.Name!, CreatedAt = now };
            db.Robots.Add(robot);
            db.RobotAuditEvents.Add(new RobotAuditEvent
            {
                Id = Guid.NewGuid(),
                RobotId = robot.Id,
                OccurredAt = now,
                Type = RobotAuditTypes.RobotCreated,
                ActorUserId = user.Id
            });
        }
        var credential = new RobotCredential { Id = Guid.NewGuid(), RobotId = robot.Id, SecretHash = pair.CredentialHash, CreatedAt = now };
        db.RobotCredentials.Add(credential);
        robot.EnrolledAt = now;
        pair.ClaimedAt = now;
        pair.Robot = robot;
        pair.Credential = credential;
        db.RobotAuditEvents.Add(new RobotAuditEvent
        {
            Id = Guid.NewGuid(),
            RobotId = robot.Id,
            OccurredAt = now,
            Type = RobotAuditTypes.Enrolled,
            ActorUserId = null
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(RobotResponse.FromEntity(robot));
    }
}
