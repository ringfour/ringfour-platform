using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Apps;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Development;

public static class AppDevelopmentEndpoints
{
    public static void MapAppDevelopmentEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/v1").RequireAuthorization();
        g.MapGet("/robots/{robotId:guid}/app-development", Get);
        g.MapPost("/robots/{robotId:guid}/app-development", Open);
        g.MapDelete("/robots/{robotId:guid}/app-development", Close);
        g.MapPost("/robots/{robotId:guid}/app-development/grants", Grant);
        routes.MapPut("/v1/device/app-development/endpoint", PublishEndpoint);
        routes.MapGet("/v1/device/app-development", Poll);
        routes.MapPost("/v1/device/app-development/{sessionId:guid}/source", Source);
    }
    internal static async Task<AppDevelopmentSession?> Current(RingfourDbContext db, Guid robot, CancellationToken ct)
        => await db.AppDevelopmentSessions.SingleOrDefaultAsync(x => x.RobotId == robot && x.ClosedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, ct);

    private static JsonObject Json(AppDevelopmentSession s) => new()
    {
        ["session_id"] = s.Id.ToString(), ["robot_id"] = s.RobotId.ToString(), ["installation_id"] = s.InstallationId.ToString(),
        ["owner_user_id"] = s.OwnerUserId.ToString(), ["created_at"] = AppProtocol.Timestamp(s.CreatedAt),
        ["expires_at"] = AppProtocol.Timestamp(s.ExpiresAt), ["state"] = s.ClosedAt is not null ? "closed" : s.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : "open",
        ["generation"] = s.Generation, ["archive_sha256"] = s.ArchiveSha256
    };
    private static async Task<IResult> Get(Guid robotId, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await RobotAccess.RequireRobotAccess(c, db, robotId, OrgRole.Operator, ct);
        var s = await Current(db, robotId, ct);
        return Results.Json(new { session = s is null ? null : Json(s) });
    }
    private static async Task<IResult> Open(Guid robotId, HttpContext c, RingfourDbContext db, DeviceConnectionManager manager, CancellationToken ct)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(c, db, robotId, OrgRole.Developer, ct);
        var body = await AppProtocol.Read(c.Request, "development_open", ct);
        return await AppCatalogEndpoints.Mutate(c, db, user, robot.OrganizationId, "development.open", robotId.ToString(), body, 201, async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id={robotId} FOR UPDATE", ct);
            if (await Current(db, robotId, ct) is not null) throw ApiException.Conflict("execution_conflict", "A development session is already open.");
            await AppInstallationEndpoints.RequireIdle(db, robotId, ct);
            await AppInstallationEndpoints.RequireNoPendingMutation(db, robotId, ct);
            if (manager.Get(robotId) is null) throw ApiException.Conflict("robot_unavailable", "The robot must be online.");
            var id = Guid.Parse(body["installation_id"]!.GetValue<string>());
            if (!await db.AppInstallations.AnyAsync(x => x.Id == id && x.RobotId == robotId && x.InstalledBinding != null && x.RemovedAt == null, ct))
                throw ApiException.NotFound("Install the development image on this robot first.");
            var endpoint = await Endpoint(db, robotId, ct);
            if (endpoint is null) throw ApiException.Conflict("local_endpoint_unavailable", "Enable and configure the Daemon development HTTPS listener first.");
            var now = DateTimeOffset.UtcNow;
            await db.AppDevelopmentSessions.Where(x => x.RobotId == robotId && x.ClosedAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClosedAt, now), ct);
            var session = new AppDevelopmentSession { Id = Guid.NewGuid(), RobotId = robotId, InstallationId = id, OwnerUserId = user.Id,
                CreatedAt = now, ExpiresAt = now.AddSeconds(AppProtocol.Integer(body["ttl_seconds"]!)) };
            db.AppDevelopmentSessions.Add(session);
            return Json(session);
        }, ct);
    }
    private static async Task<(AppDevelopmentSession Session, User Actor, Robot Robot)> Own(Guid robotId, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        var (user, robot) = await RobotAccess.RequireRobotAccess(c, db, robotId, OrgRole.Developer, ct);
        var session = await Current(db, robotId, ct) ?? throw ApiException.NotFound("No open development session.");
        var membership = await IdentityAccess.RequireMembership(db, robot.OrganizationId, user.Id, OrgRole.Developer, ct);
        if (session.OwnerUserId != user.Id && membership.Role != OrgRole.Owner) throw ApiException.Forbidden("Only the session owner or organization Owner may control this session.");
        return (session, user, robot);
    }
    private static async Task<IResult> Close(Guid robotId, Guid expected_session_id, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id={robotId} FOR UPDATE", ct);
        var (session, _, _) = await Own(robotId, c, db, ct);
        if (session.Id != expected_session_id) throw ApiException.Conflict("session_replaced", "Development session changed; refresh before ending it.");
        session.ClosedAt = DateTimeOffset.UtcNow; session.GrantHash = null; session.GrantExpiresAt = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.NoContent();
    }
    private static async Task<AppDevelopmentEndpoint?> Endpoint(RingfourDbContext db, Guid robot, CancellationToken ct)
        => await db.AppDevelopmentEndpoints.SingleOrDefaultAsync(e => e.RobotId == robot && e.UpdatedAt > DateTimeOffset.UtcNow.AddSeconds(-30) && db.RobotCredentials.Any(c => c.Id == e.CredentialId && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > DateTimeOffset.UtcNow)), ct);
    private static async Task<IResult> Grant(Guid robotId, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        var body = await AppProtocol.Read(c.Request, "development_grant", ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id={robotId} FOR UPDATE", ct);
        var (s, _, _) = await Own(robotId, c, db, ct);
        if (s.Id.ToString() != body["session_id"]!.GetValue<string>()) throw ApiException.Conflict("session_replaced", "Development session changed; connect again.");
        var endpoint = await Endpoint(db, robotId, ct) ?? throw ApiException.Conflict("local_endpoint_unavailable", "The development listener is unavailable.");
        s.GrantHash = body["token_sha256"]!.GetValue<string>();
        s.GrantExpiresAt = new[] { s.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5) }.Min();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.Json(new { session = Json(s), daemon_url = endpoint.DaemonUrl, certificate_sha256 = endpoint.CertificateSha256,
            grant_expires_at = AppProtocol.Timestamp(s.GrantExpiresAt.Value) });
    }
    private static async Task<IResult> PublishEndpoint(HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var robot = await AppInventoryEndpoints.Authenticate(c, db, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id={robot.Id} FOR UPDATE", ct);
        await AppInventoryEndpoints.Authenticate(c, db, ct);
        var body = await AppProtocol.Read(c.Request, "development_endpoint", ct);
        var url = body["daemon_url"]!.GetValue<string>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "")
            throw ApiException.InvalidInput("Development endpoint must be an HTTPS origin without credentials.");
        var endpoint = await db.AppDevelopmentEndpoints.SingleOrDefaultAsync(x => x.RobotId == robot.Id, ct);
        if (endpoint is null) { endpoint = new AppDevelopmentEndpoint { RobotId = robot.Id }; db.AppDevelopmentEndpoints.Add(endpoint); }
        endpoint.CredentialId = Guid.Parse(c.Request.Headers["x-ringfour-credential-id"].ToString());
        endpoint.DaemonUrl = url.TrimEnd('/'); endpoint.CertificateSha256 = body["certificate_sha256"]!.GetValue<string>(); endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.NoContent();
    }
    private static async Task<IResult> Poll(HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        var robot = await AppInventoryEndpoints.Authenticate(c, db, ct);
        var s = await Current(db, robot.Id, ct);
        // A removed/demoted session owner cannot keep using cached local authority.
        if (s is not null && !await db.Memberships.AnyAsync(m => m.OrganizationId == robot.OrganizationId && m.UserId == s.OwnerUserId && (m.Role == OrgRole.Developer || m.Role == OrgRole.Owner), ct)) s = null;
        if (s is null) return Results.Json(new { session = (object?)null });
        var value = Json(s); value["grant_hash"] = s.GrantHash;
        value["grant_expires_at"] = s.GrantExpiresAt is null ? null : AppProtocol.Timestamp(s.GrantExpiresAt.Value);
        return Results.Json(new { session = value });
    }
    private static async Task<IResult> Source(Guid sessionId, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var robot = await AppInventoryEndpoints.Authenticate(c, db, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id={robot.Id} FOR UPDATE", ct);
        await AppInventoryEndpoints.Authenticate(c, db, ct);
        var s = await Current(db, robot.Id, ct);
        if (s is null || s.Id != sessionId) throw ApiException.NotFound("Development session not found.");
        var body = await AppProtocol.Read(c.Request, "development_source", ct);
        var operation = Guid.Parse(body["operation_id"]!.GetValue<string>()); var digest = body["archive_sha256"]!.GetValue<string>();
        var generation = checked((int)AppProtocol.Integer(body["generation"]!));
        if (s.SourceOperationId == operation && s.Generation == generation && s.ArchiveSha256 == digest) return Results.NoContent();
        if (AppProtocol.Integer(body["base_generation"]!) != s.Generation || generation != s.Generation + 1)
            throw ApiException.Conflict("stale_generation", "Source generation must advance exactly once.");
        s.Generation = generation; s.ArchiveSha256 = digest; s.SourceOperationId = operation;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.NoContent();
    }
    internal static async Task<JsonObject?> StartSnapshot(HttpContext c, RingfourDbContext db, Robot robot, User user, Guid installationId, JsonObject body, CancellationToken ct)
    {
        var session = await Current(db, robot.Id, ct);
        if (body["development_session_id"] is null)
        {
            if (session is not null) throw ApiException.Conflict("execution_conflict", "End development mode before starting a release run.");
            return null;
        }
        if (session is null || session.Id.ToString() != body["development_session_id"]!.GetValue<string>() || session.InstallationId != installationId || session.Generation == 0)
            throw ApiException.Conflict("stale_generation", "An open development session with committed source is required.");
        await Own(robot.Id, c, db, ct);
        return new JsonObject { ["session_id"] = session.Id.ToString(), ["generation"] = session.Generation, ["archive_sha256"] = session.ArchiveSha256, ["expires_at"] = AppProtocol.Timestamp(session.ExpiresAt) };
    }
}
