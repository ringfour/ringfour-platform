using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

public static class AppCatalogEndpoints
{
    public static void MapAppCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1").RequireAuthorization().WithMetadata(new AppErrorResponse());
        group.MapPost("/organizations/{orgId:guid}/app-revisions", Publish);
        group.MapGet("/store/apps", Search);
        group.MapGet("/apps/{appId:guid}", GetApp);
        group.MapGet("/apps/{appId:guid}/revisions", Revisions);
        group.MapGet("/app-revisions/{revisionId:guid}", GetRevision);
        group.MapPost("/app-revisions/{revisionId:guid}/shares", Share);
        group.MapPost("/app-shares/{shareId:guid}/revoke", Revoke);
        group.MapPost("/organizations/{orgId:guid}/app-imports", Import);
        group.MapGet("/organizations/{orgId:guid}/app-imports", Imports);
    }

    private static Task<User> Caller(HttpContext context, RingfourDbContext db, CancellationToken ct) =>
        IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);

    private static IQueryable<AppRevision> Visible(RingfourDbContext db, Guid userId)
    {
        var organizations = db.Memberships.Where(x => x.UserId == userId).Select(x => x.OrganizationId);
        var now = DateTimeOffset.UtcNow;
        return db.AppRevisions.AsNoTracking().Where(r =>
            (r.Visibility == "listed" && organizations.Any()) || organizations.Contains(r.PublisherOrganizationId) ||
            db.AppImports.Any(i => i.RevisionId == r.Id && organizations.Contains(i.OrganizationId)) ||
            db.AppShares.Any(s => s.RevisionId == r.Id && organizations.Contains(s.DestinationOrganizationId) && s.RevokedAt == null && s.ExpiresAt > now));
    }

    internal static async Task<IResult> Mutate(HttpContext context, RingfourDbContext db, User actor,
        Guid organization, string route, string target, JsonObject body, int status, Func<Task<JsonObject>> action, CancellationToken ct)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (context.Request.Headers["Idempotency-Key"].Count != 1 || key.Length is < 1 or > 200 || key.Any(char.IsControl))
            throw ApiException.InvalidInput("One Idempotency-Key of 1–200 characters is required.");
        var fingerprint = AppProtocol.Digest(body);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize this workspace without blocking FK reads by a reciprocal share.
        await db.Organizations.FromSqlInterpolated($"SELECT * FROM organizations WHERE id = {organization} FOR NO KEY UPDATE").SingleAsync(ct);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization && x.UserId == actor.Id, ct);
        if (membership is null) throw ApiException.NotFound("Workspace is not accessible.");
        if (membership.Role < OrgRole.Developer) throw ApiException.Forbidden("This action requires Developer authority.");
        var previous = await db.AppIdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x =>
            x.PrincipalUserId == actor.Id && x.Route == route && x.Target == target && x.Key == key, ct);
        if (previous is not null)
        {
            if (previous.RequestFingerprint != fingerprint) throw ApiException.Conflict("idempotency_conflict", "The key was used for different input.");
            var outcome = JsonNode.Parse(previous.Outcome)!;
            return Results.Json(outcome["body"]!.DeepClone(), statusCode: outcome["status"]!.GetValue<int>());
        }
        var response = await action();
        db.AppIdempotencyRecords.Add(new AppIdempotencyRecord { Id = Guid.NewGuid(), PrincipalUserId = actor.Id,
            Route = route, Target = target, Key = key, RequestFingerprint = fingerprint,
            Outcome = new JsonObject { ["status"] = status, ["body"] = response.DeepClone() }.ToJsonString(), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(response, statusCode: status);
    }

    private static async Task<IResult> Publish(Guid orgId, HttpContext context, RingfourDbContext db, AppRegistry registry, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Developer, ct);
        var body = await AppProtocol.Read(context.Request, "request_publish", ct);
        return await Mutate(context, db, user, orgId, "publish", orgId.ToString(), body, 201, async () =>
        {
            var manifest = AppManifest.Validate(body["manifest"]!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var key = manifest["app_key"]!.GetValue<string>(); var release = manifest["release"]!.GetValue<string>();
            var digest = "sha256:" + AppProtocol.Digest(manifest); var visibility = body["visibility"]!.GetValue<string>();
            var app = await db.Apps.SingleOrDefaultAsync(x => x.PublisherOrganizationId == orgId && x.AppKey == key, ct);
            var existing = app is null ? null : await db.AppRevisions.SingleOrDefaultAsync(x => x.AppId == app.Id && x.Release == release, ct);
            if (existing is not null)
            {
                if (existing.ManifestDigest != digest || existing.Visibility != visibility)
                    throw ApiException.Conflict("revision_conflict", "This App release already has different immutable content.");
                return RevisionJson(existing);
            }
            foreach (var target in manifest["targets"]!.AsArray()) await registry.Verify(target!.AsObject(), orgId, ct);
            var now = DateTimeOffset.UtcNow;
            if (app is null)
            {
                app = new AppRecord { Id = Guid.NewGuid(), PublisherOrganizationId = orgId, AppKey = key, CreatedByUserId = user.Id, CreatedAt = now,
                    Name = manifest["display"]!["name"]!.GetValue<string>(), Description = manifest["display"]!["description"]!.GetValue<string>() };
                db.Apps.Add(app);
            }
            if (visibility == "listed")
            { app.Name = manifest["display"]!["name"]!.GetValue<string>(); app.Description = manifest["display"]!["description"]!.GetValue<string>(); }
            var revision = new AppRevision { Id = Guid.NewGuid(), AppId = app.Id, PublisherOrganizationId = orgId,
                Release = release, Manifest = manifest.ToJsonString(), ManifestDigest = digest, Visibility = visibility, PublishedByUserId = user.Id, PublishedAt = now };
            db.AppRevisions.Add(revision);
            return RevisionJson(revision);
        }, ct);
    }

    private static int Limit(int? limit)
    { if (limit is < 1 or > 100) throw ApiException.InvalidInput("limit must be between 1 and 100."); return limit ?? 50; }
    private static (DateTimeOffset? At, Guid? Id) Cursor(string? cursor)
    { if (cursor?.Length > 1024) throw ApiException.InvalidInput("Invalid cursor."); return Pagination.DecodeCursor(cursor); }

    private static async Task<IResult> Search([FromQuery(Name = "organization_id")] Guid organizationId, string? q, string? cursor, int? limit, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        await IdentityAccess.RequireMembership(db, organizationId, user.Id, OrgRole.Operator, ct);
        if (q?.Length > 200) throw ApiException.InvalidInput("Search exceeds 200 characters.");
        var (at, id) = Cursor(cursor); var size = Limit(limit);
        var query = db.Apps.AsNoTracking().Where(a => db.AppRevisions.Any(r => r.AppId == a.Id && r.Visibility == "listed"));
        if (!string.IsNullOrEmpty(q))
        {
            var term = q.ToLowerInvariant();
            query = query.Where(a => a.Name.ToLower().Contains(term) || a.AppKey.ToLower().Contains(term) || a.Description.ToLower().Contains(term));
        }
        if (at is not null) query = query.Where(a => a.CreatedAt > at || (a.CreatedAt == at && a.Id.CompareTo(id!.Value) > 0));
        var rows = await query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();
        return Results.Json(new { items = page.Select(AppJson), next_cursor = Pagination.NextCursor(rows.Count > size, page, x => x.CreatedAt, x => x.Id) });
    }

    private static async Task<IResult> GetApp(Guid appId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        if (!await Visible(db, user.Id).AnyAsync(r => r.AppId == appId, ct)) throw ApiException.NotFound("App is not accessible.");
        return Results.Json(AppJson(await db.Apps.AsNoTracking().SingleAsync(x => x.Id == appId, ct)));
    }

    private static async Task<IResult> GetRevision(Guid revisionId, [FromQuery(Name = "share_id")] Guid? shareId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        var revision = await Visible(db, user.Id).SingleOrDefaultAsync(x => x.Id == revisionId, ct);
        if (revision is null) throw ApiException.NotFound("Revision is not accessible.");
        return Results.Json(RevisionJson(revision));
    }

    private static async Task<IResult> Revisions(Guid appId, string? cursor, int? limit, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct); var query = Visible(db, user.Id).Where(r => r.AppId == appId);
        if (!await query.AnyAsync(ct)) throw ApiException.NotFound("App is not accessible.");
        var (at, id) = Cursor(cursor); var size = Limit(limit);
        if (at is not null) query = query.Where(r => r.PublishedAt > at || (r.PublishedAt == at && r.Id.CompareTo(id!.Value) > 0));
        var rows = await query.OrderBy(r => r.PublishedAt).ThenBy(r => r.Id).Take(size + 1).ToListAsync(ct); var page = rows.Take(size).ToList();
        return Results.Json(new { items = page.Select(RevisionJson), next_cursor = Pagination.NextCursor(rows.Count > size, page, x => x.PublishedAt, x => x.Id) });
    }

    private static async Task<IResult> Share(Guid revisionId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        var revision = await db.AppRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId, ct) ?? throw ApiException.NotFound("Revision is not accessible.");
        await IdentityAccess.RequireMembership(db, revision.PublisherOrganizationId, user.Id, OrgRole.Developer, ct);
        var body = await AppProtocol.Read(context.Request, "request_share", ct);
        return await Mutate(context, db, user, revision.PublisherOrganizationId, "share", revisionId.ToString(), body, 201, async () =>
        {
            var destination = Guid.Parse(body["destination_organization_id"]!.GetValue<string>());
            var expires = DateTimeOffset.Parse(body["expires_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            var now = DateTimeOffset.UtcNow;
            if (expires <= now) throw ApiException.InvalidInput("Share expiry must be in the future.");
            if (!await db.Organizations.AnyAsync(x => x.Id == destination, ct)) throw ApiException.NotFound("Destination workspace does not exist.");
            var share = new AppShare { Id = Guid.NewGuid(), RevisionId = revision.Id, AppId = revision.AppId, PublisherOrganizationId = revision.PublisherOrganizationId,
                DestinationOrganizationId = destination, GrantedByUserId = user.Id, GrantedAt = now, ExpiresAt = expires };
            db.AppShares.Add(share); return ShareJson(share);
        }, ct);
    }

    private static async Task<IResult> Revoke(Guid shareId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        var row = await db.AppShares.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shareId, ct) ?? throw ApiException.NotFound("Share is not accessible.");
        await IdentityAccess.RequireMembership(db, row.PublisherOrganizationId, user.Id, OrgRole.Developer, ct);
        if (await context.Request.Body.ReadAsync(new byte[1], ct) > 0) throw ApiException.InvalidInput("Revoke accepts no body.");
        return await Mutate(context, db, user, row.PublisherOrganizationId, "revoke", shareId.ToString(), new JsonObject(), 200, async () =>
        {
            var share = await db.AppShares.FromSqlInterpolated($"SELECT * FROM app_shares WHERE id = {shareId} FOR UPDATE").SingleAsync(ct);
            share.RevokedAt ??= DateTimeOffset.UtcNow; return ShareJson(share);
        }, ct);
    }

    private static async Task<IResult> Import(Guid orgId, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Developer, ct);
        var body = await AppProtocol.Read(context.Request, "request_import", ct);
        return await Mutate(context, db, user, orgId, "import", orgId.ToString(), body, 201, async () =>
        {
            return ImportJson(await EnsureImport(db, user, orgId, body, ct));
        }, ct);
    }

    internal static async Task<AppImport> EnsureImport(RingfourDbContext db, User user, Guid orgId, JsonObject body, CancellationToken ct)
    {
        var revisionId = Guid.Parse(body["revision_id"]!.GetValue<string>());
        var existing = await db.AppImports.SingleOrDefaultAsync(x => x.OrganizationId == orgId && x.RevisionId == revisionId, ct);
        if (existing is not null) return existing;
        var revision = await db.AppRevisions.SingleOrDefaultAsync(x => x.Id == revisionId, ct) ?? throw ApiException.NotFound("Revision is not accessible.");
        Guid? shareId = null;
        if (revision.Visibility != "listed")
        {
            if (body["share_id"] is null)
            {
                if (!await Visible(db, user.Id).AnyAsync(x => x.Id == revisionId, ct)) throw ApiException.NotFound("Revision is not accessible.");
                throw ApiException.Conflict("share_required", "An explicit destination-bound share is required.");
            }
            shareId = Guid.Parse(body["share_id"]!.GetValue<string>());
            var share = await db.AppShares.FromSqlInterpolated($"SELECT * FROM app_shares WHERE id = {shareId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (share is null || share.RevisionId != revision.Id || share.PublisherOrganizationId != revision.PublisherOrganizationId || share.DestinationOrganizationId != orgId)
                throw ApiException.NotFound("Share is not accessible.");
            if (share.RevokedAt is not null) throw ApiException.Conflict("share_revoked", "Share was revoked.");
            if (share.ExpiresAt <= DateTimeOffset.UtcNow) throw ApiException.Conflict("share_expired", "Share has expired.");
        }
        var imported = new AppImport { Id = Guid.NewGuid(), OrganizationId = orgId, AppId = revision.AppId, RevisionId = revision.Id,
            PublisherOrganizationId = revision.PublisherOrganizationId, SourceKind = shareId is null ? "store" : "direct_share", ShareId = shareId,
            ImportedByUserId = user.Id, ImportedAt = DateTimeOffset.UtcNow };
        db.AppImports.Add(imported); return imported;
    }

    private static async Task<IResult> Imports(Guid orgId, string? cursor, int? limit, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await Caller(context, db, ct); await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Operator, ct);
        var (at, id) = Cursor(cursor); var size = Limit(limit); var query = db.AppImports.AsNoTracking().Where(x => x.OrganizationId == orgId);
        if (at is not null) query = query.Where(x => x.ImportedAt > at || (x.ImportedAt == at && x.Id.CompareTo(id!.Value) > 0));
        var rows = await query.OrderBy(x => x.ImportedAt).ThenBy(x => x.Id).Take(size + 1).ToListAsync(ct); var page = rows.Take(size).ToList();
        return Results.Json(new { items = page.Select(ImportJson), next_cursor = Pagination.NextCursor(rows.Count > size, page, x => x.ImportedAt, x => x.Id) });
    }

    internal static JsonObject RevisionJson(AppRevision r) => new() { ["id"] = r.Id.ToString(), ["app_id"] = r.AppId.ToString(),
        ["publisher_organization_id"] = r.PublisherOrganizationId.ToString(), ["manifest"] = JsonNode.Parse(r.Manifest), ["manifest_digest"] = r.ManifestDigest,
        ["visibility"] = r.Visibility, ["published_by"] = r.PublishedByUserId.ToString(), ["published_at"] = AppProtocol.Timestamp(r.PublishedAt) };
    private static JsonObject AppJson(AppRecord a) => new() { ["id"] = a.Id.ToString(), ["publisher_organization_id"] = a.PublisherOrganizationId.ToString(),
        ["app_key"] = a.AppKey, ["name"] = a.Name, ["description"] = a.Description, ["created_by"] = a.CreatedByUserId.ToString(), ["created_at"] = AppProtocol.Timestamp(a.CreatedAt) };
    private static JsonObject ShareJson(AppShare s) => new() { ["id"] = s.Id.ToString(), ["app_id"] = s.AppId.ToString(), ["revision_id"] = s.RevisionId.ToString(),
        ["publisher_organization_id"] = s.PublisherOrganizationId.ToString(), ["destination_organization_id"] = s.DestinationOrganizationId.ToString(),
        ["granted_by"] = s.GrantedByUserId.ToString(), ["granted_at"] = AppProtocol.Timestamp(s.GrantedAt), ["expires_at"] = AppProtocol.Timestamp(s.ExpiresAt),
        ["revoked_at"] = s.RevokedAt is null ? null : AppProtocol.Timestamp(s.RevokedAt.Value) };
    private static JsonObject ImportJson(AppImport i) => new() { ["id"] = i.Id.ToString(), ["organization_id"] = i.OrganizationId.ToString(), ["app_id"] = i.AppId.ToString(),
        ["revision_id"] = i.RevisionId.ToString(), ["publisher_organization_id"] = i.PublisherOrganizationId.ToString(),
        ["source"] = i.ShareId is null ? new JsonObject { ["kind"] = "store" } : new JsonObject { ["kind"] = "direct_share", ["share_id"] = i.ShareId.ToString() },
        ["imported_by"] = i.ImportedByUserId.ToString(), ["imported_at"] = AppProtocol.Timestamp(i.ImportedAt) };
}
