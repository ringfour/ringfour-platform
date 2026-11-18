using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.MissionUi;

public static class MissionUiEndpoints
{
    private const string CookieName = "ringfour_ui_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromSeconds(45);

    public static void MapMissionUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/v1").RequireAuthorization()
            .MapPost("/executions/{executionId:guid}/ui-sessions", CreateSession);
        endpoints.MapGet("/ui/bootstrap/{sessionId:guid}", BootstrapPage);
        endpoints.MapPost("/v1/ui-sessions/exchange", ExchangeBootstrap);
    }

    private static async Task<IResult> CreateSession(
        HttpContext context, RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        var execution = await db.ScriptExecutions.Include(item => item.Robot)
            .SingleOrDefaultAsync(item => item.Id == executionId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var (user, robot) = await RobotAccess.RequireRobotAccess(
            context, db, execution.RobotId, OrgRole.Operator, ct);
        if (execution.RobotId != robot.Id || execution.State != "running")
            throw ApiException.Conflict("execution_not_running", "Mission UI is available only while the execution is running.");

        var uiOrigin = ConfiguredUiOrigin(context.RequestServices.GetRequiredService<IConfiguration>(), execution.Id);
        var consoleOrigin = ConfiguredConsoleOrigin(context.RequestServices.GetRequiredService<IConfiguration>());
        if (!string.Equals(uiOrigin.Scheme, consoleOrigin.Scheme, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "mission_ui_unconfigured", "Console and mission UI must use the same scheme.");
        var now = DateTimeOffset.UtcNow;
        await db.MissionUiSessions.Where(item => item.ExpiresAt <= now || item.RevokedAt != null)
            .ExecuteDeleteAsync(ct);

        var bootstrapToken = NewToken();
        var session = new MissionUiSession
        {
            Id = Guid.NewGuid(),
            ExecutionId = execution.Id,
            OrganizationId = robot.OrganizationId,
            UserId = user.Id,
            BootstrapTokenHash = HashToken(bootstrapToken),
            BootstrapExpiresAt = now.Add(BootstrapLifetime),
            ExpiresAt = now.Add(SessionLifetime),
            CreatedAt = now,
        };
        db.MissionUiSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            session_id = session.Id,
            execution_id = execution.Id,
            ui_origin = uiOrigin.GetLeftPart(UriPartial.Authority),
            bootstrap_token = bootstrapToken,
            bootstrap_expires_at = session.BootstrapExpiresAt,
            console_origin = consoleOrigin.GetLeftPart(UriPartial.Authority),
        }, RingfourJson.Options, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> BootstrapPage(
        HttpContext context, RingfourDbContext db, IConfiguration configuration,
        Guid sessionId, CancellationToken ct)
    {
        var session = await db.MissionUiSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId, ct);
        if (session is null) throw ApiException.NotFound("The requested resource does not exist.");

        var uiOrigin = ConfiguredUiOrigin(configuration, session.ExecutionId);
        if (!RequestUsesOriginHost(context.Request, uiOrigin))
            throw ApiException.NotFound("The requested resource does not exist.");
        if (session.ExchangedAt is not null || session.RevokedAt is not null ||
            session.BootstrapExpiresAt <= DateTimeOffset.UtcNow || session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw ApiException.Conflict("ui_bootstrap_expired", "The mission UI bootstrap has expired.");

        var consoleOrigin = ConfiguredConsoleOrigin(configuration).GetLeftPart(UriPartial.Authority);
        var consoleOriginJs = JsonSerializer.Serialize(consoleOrigin, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
        });
        var sessionIdJs = JsonSerializer.Serialize(sessionId.ToString("D"));
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'nonce-{nonce}'; connect-src 'self'; " +
            $"frame-ancestors {consoleOrigin}; base-uri 'none'; form-action 'none'";

        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"><title>Mission UI</title></head>" +
            "<body><p id=\"status\">Connecting securely…</p><script nonce=\"" + nonce + "\">" +
            "const consoleOrigin=" + consoleOriginJs + ";const sessionId=" + sessionIdJs + ";" +
            "let exchanged=false;window.addEventListener('message',async event=>{" +
            "const data=event.data;if(exchanged||event.origin!==consoleOrigin||event.source!==window.parent||!data||" +
            "data.type!=='ringfour-ui-bootstrap'||data.session_id!==sessionId||typeof data.bootstrap_token!=='string')return;" +
            "exchanged=true;const status=document.getElementById('status');try{" +
            "const response=await fetch('/v1/ui-sessions/exchange',{method:'POST',credentials:'same-origin'," +
            "headers:{'Content-Type':'application/json'},body:JSON.stringify({session_id:sessionId,bootstrap_token:data.bootstrap_token})});" +
            "if(!response.ok)throw new Error('session exchange failed');" +
            "status.textContent='Session authorized. Waiting for the mission UI connection…';" +
            "}catch{status.textContent='Unable to authorize this mission UI. Close it and try again.';}});" +
            "window.parent.postMessage({type:'ringfour-ui-bootstrap-ready',session_id:sessionId},consoleOrigin);</script></body></html>";
        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    private static async Task<IResult> ExchangeBootstrap(
        HttpContext context, RingfourDbContext db, IConfiguration configuration, CancellationToken ct)
    {
        byte[] raw;
        try
        {
            raw = await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "size_limit", "UI bootstrap request is too large.");
        }

        Guid sessionId;
        string token;
        try
        {
            var body = JsonNode.Parse(raw) as JsonObject;
            if (body is null || body.Count != 2 ||
                body["session_id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var idText) ||
                !Guid.TryParseExact(idText, "D", out sessionId) || sessionId.ToString("D") != idText ||
                body["bootstrap_token"] is not JsonValue tokenValue || !tokenValue.TryGetValue<string>(out token!) ||
                !ValidToken(token))
                throw new JsonException("Invalid exchange request.");
        }
        catch (JsonException)
        {
            throw ApiException.InvalidInput("Invalid mission UI bootstrap request.");
        }

        var session = await db.MissionUiSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist.");
        var uiOrigin = ConfiguredUiOrigin(configuration, session.ExecutionId);
        if (!RequestUsesOriginHost(context.Request, uiOrigin) || !RequestOriginMatches(context.Request, uiOrigin))
            throw new ApiException(StatusCodes.Status403Forbidden, "ui_origin_denied", "Mission UI origin is not allowed.");

        var execution = await db.ScriptExecutions.Include(item => item.Robot)
            .SingleOrDefaultAsync(item => item.Id == session.ExecutionId, ct);
        if (execution is null || execution.RobotId == Guid.Empty ||
            execution.Robot.OrganizationId != session.OrganizationId || execution.State != "running")
            throw ApiException.Conflict("execution_not_running", "Mission UI is available only while the execution is running.");
        await IdentityAccess.RequireMembership(db, session.OrganizationId, session.UserId, OrgRole.Operator, ct);

        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashToken(token);
        var sessionToken = NewToken();
        var sessionHash = HashToken(sessionToken);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE mission_ui_sessions
            SET session_token_hash = {sessionHash}, exchanged_at = {now}
            WHERE id = {sessionId}
              AND bootstrap_token_hash = {tokenHash}
              AND session_token_hash IS NULL
              AND exchanged_at IS NULL
              AND revoked_at IS NULL
              AND bootstrap_expires_at > {now}
              AND expires_at > {now}
            """, ct);
        if (updated != 1)
            throw ApiException.Conflict("ui_bootstrap_expired", "The mission UI bootstrap is expired or has already been used.");

        context.Response.Cookies.Append(CookieName, sessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = uiOrigin.Scheme == Uri.UriSchemeHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = session.ExpiresAt,
            IsEssential = true,
        });
        context.Response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    private static Uri ConfiguredUiOrigin(IConfiguration configuration, Guid executionId)
    {
        var configured = configuration["MissionUi:BaseOrigin"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseOrigin) ||
            baseOrigin.Scheme is not ("http" or "https") || baseOrigin.UserInfo.Length != 0 ||
            baseOrigin.AbsolutePath != "/" || baseOrigin.Query.Length != 0 || baseOrigin.Fragment.Length != 0)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "mission_ui_unconfigured", "Mission UI origin is not configured.");
        return new UriBuilder(baseOrigin) { Host = $"{executionId:D}.{baseOrigin.Host}" }.Uri;
    }

    private static Uri ConfiguredConsoleOrigin(IConfiguration configuration)
    {
        var configured = configuration["MissionUi:ConsoleOrigin"] ?? configuration["Email:ConsoleOrigin"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var consoleOrigin) ||
            consoleOrigin.Scheme is not ("http" or "https") || consoleOrigin.UserInfo.Length != 0 ||
            consoleOrigin.AbsolutePath != "/" || consoleOrigin.Query.Length != 0 || consoleOrigin.Fragment.Length != 0)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "mission_ui_unconfigured", "Console origin is not configured.");
        return consoleOrigin;
    }

    private static bool RequestUsesOriginHost(HttpRequest request, Uri origin)
    {
        var port = request.Host.Port ?? (origin.Scheme == Uri.UriSchemeHttps ? 443 : 80);
        return string.Equals(request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
            port == origin.Port;
    }

    private static bool RequestOriginMatches(HttpRequest request, Uri origin)
    {
        if (!Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var supplied)) return false;
        return string.Equals(supplied.GetLeftPart(UriPartial.Authority),
            origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidToken(string token) => token.Length == 43 &&
        token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] HashToken(string token)
    {
        var encoded = token.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        return SHA256.HashData(Convert.FromBase64String(encoded));
    }
}
