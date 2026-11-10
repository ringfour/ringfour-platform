using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Robots;

public static class RobotEndpoints
{
    public static void MapRobotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();

        group.MapPost("/organizations/{orgId:guid}/robots", CreateRobot);
        group.MapGet("/organizations/{orgId:guid}/robots", ListRobots);
        group.MapGet("/robots/{robotId:guid}", GetRobot);
        group.MapPost("/robots/{robotId:guid}/enrollment-tokens", IssueEnrollmentToken);
        group.MapGet("/robots/{robotId:guid}/credentials", ListCredentials);
        group.MapPost("/robots/{robotId:guid}/credentials", RotateCredential);
        group.MapPost("/robots/{robotId:guid}/credentials/{credentialId:guid}/revoke", RevokeCredential);

        // Unauthenticated by design: possession of the token or credential is
        // the proof. Mounted outside the authorized group on purpose.
        var device = endpoints.MapGroup("/v1/device");
        device.MapPost("/enroll", Enroll);
        device.MapPost("/authenticate", AuthenticateDevice);
    }

    private static string NewSecret(string prefix) =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))
            .ToLowerInvariant();

    private static void Audit(
        RingfourDbContext db, Guid robotId, string type, Guid? actorUserId, DateTimeOffset now) =>
        db.RobotAuditEvents.Add(new RobotAuditEvent
        {
            Id = Guid.NewGuid(),
            RobotId = robotId,
            OccurredAt = now,
            Type = type,
            ActorUserId = actorUserId,
        });

    private static async Task<(User User, Robot Robot)> RequireRobotAccess(
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

    private static async Task<IResult> CreateRobot(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        CreateRobotRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Name is null || string.IsNullOrWhiteSpace(request.Name))
        {
            throw ApiException.InvalidInput("Robot name must be nonblank.");
        }
        if (request.Name.Length > 200)
        {
            throw ApiException.InvalidInput("Robot name must not exceed 200 characters.");
        }
        if (request.ProfileRef is not null && request.ProfileRef.Length > 500)
        {
            throw ApiException.InvalidInput("profile_ref must not exceed 500 characters.");
        }

        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(
            db, orgId, user.Id, OrgRole.Owner, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var robot = new Robot
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = request.Name,
            CreatedAt = now,
            EnrolledAt = null,
            ProfileRef = request.ProfileRef,
        };
        db.Robots.Add(robot);
        Audit(db, robot.Id, RobotAuditTypes.RobotCreated, user.Id, now);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/v1/robots/{robot.Id}", RobotResponse.FromEntity(robot));
    }

    private static async Task<RobotListResponse> ListRobots(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(
            db, orgId, user.Id, OrgRole.Operator, cancellationToken);

        var (createdAfter, idAfter) = Pagination.DecodeCursor(cursor);
        var pageSize = Pagination.ClampLimit(limit);

        var query = db.Robots
            .Where(robot => robot.OrganizationId == orgId)
            .OrderBy(robot => robot.CreatedAt)
            .ThenBy(robot => robot.Id)
            .AsQueryable();
        if (createdAfter is not null)
        {
            query = query.Where(robot =>
                robot.CreatedAt > createdAfter ||
                (robot.CreatedAt == createdAfter && robot.Id.CompareTo(idAfter!.Value) > 0));
        }

        var robots = await query.Take(pageSize + 1).ToListAsync(cancellationToken);
        var page = robots.Take(pageSize).ToList();
        return new RobotListResponse(
            page.Select(RobotResponse.FromEntity).ToList(),
            Pagination.NextCursor(
                robots.Count > pageSize, page, robot => robot.CreatedAt, robot => robot.Id));
    }

    private static async Task<RobotResponse> GetRobot(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (_, robot) = await RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Operator, cancellationToken);
        return RobotResponse.FromEntity(robot);
    }

    private static async Task<IResult> IssueEnrollmentToken(
        HttpContext httpContext,
        RingfourDbContext db,
        IConfiguration configuration,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (user, robot) = await RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Owner, cancellationToken);

        var ttlMinutes = configuration.GetValue("Enrollment:TokenTtlMinutes", 15);
        var now = DateTimeOffset.UtcNow;
        var tokenValue = NewSecret("rft_");
        var token = new EnrollmentToken
        {
            Id = Guid.NewGuid(),
            RobotId = robot.Id,
            TokenHash = HashSecret(tokenValue),
            IssuedByUserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(ttlMinutes),
            ConsumedAt = null,
        };
        db.EnrollmentTokens.Add(token);
        Audit(db, robot.Id, RobotAuditTypes.EnrollmentTokenIssued, user.Id, now);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/v1/robots/{robot.Id}/enrollment-tokens/{token.Id}",
            new EnrollmentTokenResponse(
                token.Id, robot.Id, tokenValue, token.CreatedAt, token.ExpiresAt));
    }

    private static async Task<CredentialListResponse> ListCredentials(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (_, robot) = await RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Operator, cancellationToken);
        var credentials = await db.RobotCredentials
            .Where(credential => credential.RobotId == robot.Id)
            .OrderByDescending(credential => credential.CreatedAt)
            .ThenByDescending(credential => credential.Id)
            .ToListAsync(cancellationToken);
        return new CredentialListResponse(
            credentials.Select(RobotCredentialResponse.FromEntity).ToList());
    }

    private static async Task<IResult> RotateCredential(
        HttpContext httpContext,
        RingfourDbContext db,
        IConfiguration configuration,
        Guid robotId,
        CancellationToken cancellationToken)
    {
        var (user, robot) = await RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Owner, cancellationToken);
        if (robot.EnrolledAt is null)
        {
            throw ApiException.InvalidInput(
                "The robot is not enrolled; use an enrollment token first.");
        }

        var overlapSeconds = configuration.GetValue("Enrollment:RotationOverlapSeconds", 300);
        var now = DateTimeOffset.UtcNow;

        var current = await db.RobotCredentials
            .Where(credential =>
                credential.RobotId == robot.Id &&
                credential.RevokedAt == null &&
                (credential.ExpiresAt == null || credential.ExpiresAt > now))
            .OrderByDescending(credential => credential.CreatedAt)
            .ThenByDescending(credential => credential.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is not null)
        {
            // Bound the rotation overlap: the previous credential keeps working
            // until this expiry so a daemon mid-rotation never loses access.
            var overlapEnd = now.AddSeconds(overlapSeconds);
            if (current.ExpiresAt is null || current.ExpiresAt > overlapEnd)
            {
                current.ExpiresAt = overlapEnd;
            }
        }

        var secret = NewSecret("rfs_");
        var credential = new RobotCredential
        {
            Id = Guid.NewGuid(),
            RobotId = robot.Id,
            SecretHash = HashSecret(secret),
            CreatedAt = now,
            ExpiresAt = null,
            RevokedAt = null,
        };
        db.RobotCredentials.Add(credential);
        Audit(db, robot.Id, RobotAuditTypes.CredentialRotated, user.Id, now);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/v1/robots/{robot.Id}/credentials/{credential.Id}",
            new RotateCredentialResponse(RobotCredentialResponse.FromEntity(credential), secret));
    }

    private static async Task<RobotCredentialResponse> RevokeCredential(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid robotId,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var (user, robot) = await RequireRobotAccess(
            httpContext, db, robotId, OrgRole.Owner, cancellationToken);
        var credential = await db.RobotCredentials.SingleOrDefaultAsync(
            candidate => candidate.RobotId == robot.Id && candidate.Id == credentialId,
            cancellationToken);
        if (credential is null)
        {
            throw ApiException.NotFound(
                "The requested resource does not exist in this organization.");
        }

        if (credential.RevokedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            credential.RevokedAt = now;
            Audit(db, robot.Id, RobotAuditTypes.CredentialRevoked, user.Id, now);
            await db.SaveChangesAsync(cancellationToken);
        }
        return RobotCredentialResponse.FromEntity(credential);
    }

    // Keyless row returned by the atomic token-consumption UPDATE.
    private sealed record ConsumedToken(Guid Id, Guid RobotId);

    private static async Task<EnrollResponse> Enroll(
        RingfourDbContext db,
        EnrollRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Token is null || string.IsNullOrWhiteSpace(request.Token))
        {
            throw ApiException.InvalidInput("token is required.");
        }

        var tokenHash = HashSecret(request.Token);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        // Atomic consumption: concurrent attempts serialize on the row lock;
        // only the first sees consumed_at still null. Expired tokens match no
        // rows either, so unknown/expired/consumed all collapse to the same
        // invalid_token failure.
        var consumed = db.Database
            .SqlQuery<ConsumedToken>($"""
                UPDATE enrollment_tokens
                SET consumed_at = now()
                WHERE token_hash = {tokenHash} AND consumed_at IS NULL AND expires_at > now()
                RETURNING id AS "Id", robot_id AS "RobotId"
                """)
            .AsEnumerable()
            .SingleOrDefault();
        if (consumed is null)
        {
            throw ApiException.InvalidToken(
                "The enrollment token is invalid, expired, or already used.");
        }

        var robot = await db.Robots.SingleAsync(
            candidate => candidate.Id == consumed.RobotId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        robot.EnrolledAt ??= now;

        var secret = NewSecret("rfs_");
        var credential = new RobotCredential
        {
            Id = Guid.NewGuid(),
            RobotId = robot.Id,
            SecretHash = HashSecret(secret),
            CreatedAt = now,
            ExpiresAt = null,
            RevokedAt = null,
        };
        db.RobotCredentials.Add(credential);
        Audit(db, robot.Id, RobotAuditTypes.Enrolled, null, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EnrollResponse(robot.Id, robot.OrganizationId, credential.Id, secret);
    }

    private static async Task<DeviceAuthenticateResponse> AuthenticateDevice(
        RingfourDbContext db,
        DeviceAuthenticateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Secret is null || string.IsNullOrWhiteSpace(request.Secret))
        {
            throw ApiException.InvalidInput("secret is required.");
        }

        var credential = await db.RobotCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == request.CredentialId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (credential is null ||
            credential.RevokedAt is not null ||
            (credential.ExpiresAt is not null && credential.ExpiresAt <= now) ||
            !FixedTimeEquals(HashSecret(request.Secret), credential.SecretHash))
        {
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        }

        var robot = await db.Robots.SingleAsync(
            candidate => candidate.Id == credential.RobotId, cancellationToken);
        return new DeviceAuthenticateResponse(robot.Id, robot.OrganizationId, credential.Id);
    }

    private static bool FixedTimeEquals(string leftHex, string rightHex) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(leftHex), Convert.FromHexString(rightHex));
}
