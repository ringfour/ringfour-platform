using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

public sealed class ManagedAppRegistry : IDisposable
{
    private readonly X509Certificate2? certificate;
    public string? Host { get; }
    public string Endpoint { get; }
    public string Service { get; }
    private string Issuer { get; }

    public ManagedAppRegistry(IConfiguration config)
    {
        var settings = config.GetSection("Apps:ManagedRegistry");
        Host = settings.GetValue("Enabled", false) ? settings["Host"] ?? "registry.ringfour.eu" : null;
        Endpoint = settings["Endpoint"] ?? "http://app-registry:5000";
        Service = settings["Service"] ?? "ringfour-app-registry";
        Issuer = settings["Issuer"] ?? "ringfour-platform";
        if (Host is null) return;
        certificate = X509Certificate2.CreateFromPemFile(
            settings["CertificatePath"] ?? "/run/registry-signing/cert.pem",
            settings["KeyPath"] ?? "/run/registry-key/key.pem");
        using var key = certificate.GetRSAPrivateKey();
        if (key is null || key.KeySize < 2048)
            throw new InvalidOperationException("Managed registry signing requires an RSA key of at least 2048 bits.");
        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("Managed registry signing certificate is outside its validity window.");
    }

    public void Dispose() => certificate?.Dispose();

    internal static string Repository(Guid organization, Guid app) => $"apps/{organization:D}/{app:D}";

    internal bool Owns(string image) => Host is not null && image.StartsWith(Host + "/", StringComparison.Ordinal);

    internal void RequireEnabled()
    {
        if (Host is null) throw ApiException.NotFound("Managed App registry is disabled.");
    }

    internal string Issue(string subject, TimeSpan lifetime, params RegistryScope[] access) =>
        Sign(subject, Service, DateTimeOffset.UtcNow.Add(lifetime), access);

    private string Sign(string subject, string audience, DateTimeOffset expiry, RegistryScope[] access)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload
        {
            ["iss"] = Issuer, ["aud"] = audience, ["sub"] = subject,
            ["iat"] = now.ToUnixTimeSeconds(), ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiry.ToUnixTimeSeconds(), ["jti"] = Guid.NewGuid().ToString(),
            ["access"] = access.Select(x => new Dictionary<string, object>
                { ["type"] = "repository", ["name"] = x.Repository, ["actions"] = x.Actions }).ToArray()
        };
        var header = new JwtHeader(new X509SigningCredentials(certificate!, SecurityAlgorithms.RsaSha256))
        {
            ["x5c"] = new[] { Convert.ToBase64String(certificate!.RawData) }
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    internal static async Task<IResult> Reserve(Guid orgId, string appKey, HttpContext context, RingfourDbContext db,
        ManagedAppRegistry registry, ILogger<ManagedAppRegistry> logger, CancellationToken ct)
    {
        registry.RequireEnabled();
        context.Response.Headers.CacheControl = "no-store";
        if (appKey.Length > 100 || !Regex.IsMatch(appKey, @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z"))
            throw ApiException.InvalidInput("Invalid App key.");
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Organizations.FromSqlInterpolated($"SELECT * FROM organizations WHERE id = {orgId} AND deleted_at IS NULL FOR NO KEY UPDATE").SingleOrDefaultAsync(ct) is null)
            throw ApiException.NotFound("Workspace is not accessible.");
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Developer, ct);
        var app = await db.Apps.SingleOrDefaultAsync(x => x.PublisherOrganizationId == orgId && x.AppKey == appKey, ct);
        if (app is null)
        {
            app = new AppRecord { Id = Guid.NewGuid(), PublisherOrganizationId = orgId, AppKey = appKey,
                Name = appKey, Description = "", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Apps.Add(app);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        var repository = Repository(orgId, app.Id);
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var credential = registry.Sign(user.Id.ToString(), registry.Service + ":upload", expires, [new(repository, ["pull", "push"])]);
        logger.LogInformation("Authorized registry upload for user {UserId}, app {AppId}, organization {OrganizationId}", user.Id, app.Id, orgId);
        return Results.Json(new { registry = registry.Host, repository = registry.Host + "/" + repository, credential, expires_at = expires });
    }

    internal static async Task<IResult> Token(HttpContext context, RingfourDbContext db, ManagedAppRegistry registry,
        ILogger<ManagedAppRegistry> logger, CancellationToken ct)
    {
        registry.RequireEnabled();
        context.Response.Headers.CacheControl = "no-store";
        var scopes = context.Request.Query["scope"];
        if (context.Request.Query["service"] != registry.Service || scopes.Count > 8 || scopes.Any(x => x?.Length > 300))
            throw ApiException.InvalidInput("Invalid registry service or scope.");

        Guid userId;
        string? uploadRepository = null;
        var login = context.Request.Headers.Authorization.ToString();
        if (login.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (!registry.TryReadUpload(login[6..], out userId, out uploadRepository))
                throw ApiException.Unauthenticated("Invalid registry upload credential.");
        }
        else
        {
            if (context.User.Identity?.IsAuthenticated != true)
                throw ApiException.Unauthenticated("Registry authentication is required.");
            var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
            userId = user.Id;
        }

        var permitted = new List<RegistryScope>();
        foreach (var scope in scopes)
        {
            var parts = scope!.Split(':');
            if (parts.Length != 3 || parts[0] != "repository" ||
                (uploadRepository is not null && uploadRepository != parts[1])) continue;
            var path = parts[1].Split('/');
            if (path.Length != 3 || path[0] != "apps" || !Guid.TryParseExact(path[1], "D", out var orgId) ||
                !Guid.TryParseExact(path[2], "D", out var appId) || parts[1] != Repository(orgId, appId)) continue;
            if (!await db.Apps.AnyAsync(x => x.Id == appId && x.PublisherOrganizationId == orgId &&
                db.Organizations.Any(o => o.Id == orgId && o.DeletedAt == null), ct)) continue;
            var memberships = db.Memberships.Where(x => x.UserId == userId &&
                db.Organizations.Any(o => o.Id == x.OrganizationId && o.DeletedAt == null));
            var role = await memberships.Where(x => x.OrganizationId == orgId).Select(x => (OrgRole?)x.Role).SingleOrDefaultAsync(ct);
            var canPull = role is not null || await db.AppImports.AnyAsync(x => x.AppId == appId &&
                memberships.Any(m => m.OrganizationId == x.OrganizationId), ct);
            var requested = parts[2].Split(',');
            var actions = new List<string>();
            if (canPull && requested.Contains("pull")) actions.Add("pull");
            if (role >= OrgRole.Developer && requested.Contains("push")) actions.Add("push");
            if (actions.Count > 0) permitted.Add(new(parts[1], actions.ToArray()));
        }
        if (scopes.Count > 0 && permitted.Count == 0)
            throw ApiException.Forbidden("No requested repository access is authorized.");
        var issued = DateTimeOffset.UtcNow;
        var token = registry.Sign(userId.ToString(), registry.Service, issued.AddMinutes(5), permitted.ToArray());
        logger.LogInformation("Issued registry token for user {UserId} with {ScopeCount} scopes", userId, permitted.Count);
        return Results.Json(new { token, expires_in = 300, issued_at = issued });
    }

    private bool TryReadUpload(string encoded, out Guid userId, out string? repository)
    {
        userId = default;
        repository = null;
        if (encoded.Length > 8192) return false;
        try
        {
            var basic = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            if (!basic.StartsWith("ringfour:", StringComparison.Ordinal)) return false;
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(basic[9..], new TokenValidationParameters
            {
                ValidIssuer = Issuer, ValidAudience = Service + ":upload", IssuerSigningKey = new X509SecurityKey(certificate!),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.Zero, ValidateIssuerSigningKey = true
            }, out var validated);
            var jwt = (JwtSecurityToken)validated;
            var access = System.Text.Json.JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
            using (access)
                repository = access.RootElement.GetProperty("access")[0].GetProperty("name").GetString();
            return repository is not null && Guid.TryParse(principal.FindFirst("sub")?.Value, out userId);
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}

internal sealed record RegistryScope(string Repository, string[] Actions);
