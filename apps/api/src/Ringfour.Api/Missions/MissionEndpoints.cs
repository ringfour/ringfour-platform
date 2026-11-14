using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Missions;

public static class MissionEndpoints
{
    public static void MapMissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();
        group.MapPost("/missions", Create);
        group.MapGet("/organizations/{organizationId:guid}/missions", List);
        group.MapPost("/missions/{missionId:guid}/revisions", Publish);
        group.MapGet("/missions/{missionId:guid}/revisions", Revisions);
        group.MapGet("/missions/{missionId:guid}/revisions/{revisionId:guid}/source", Source);
    }

    private static string Key(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw ApiException.InvalidInput("Idempotency-Key is required and must not exceed 200 characters.");
        return key;
    }

    private static async Task<JsonObject> Body(HttpContext context, CancellationToken ct)
    {
        try
        {
            return JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 8192, ct)) as JsonObject
                ?? throw new JsonException();
        }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid mission request."); }
    }

    private static string Fingerprint(object body) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, RingfourJson.Options)));

    private static object Response(Mission mission) => new
    {
        id = mission.Id,
        organization_id = mission.OrganizationId,
        name = mission.Name,
        created_by_user_id = mission.CreatedByUserId,
        created_at = mission.CreatedAt,
        latest_version = mission.LatestVersion
    };

    private static object Response(MissionRevision revision) => new
    {
        id = revision.Id,
        mission_id = revision.MissionId,
        version = revision.Version,
        script_revision_id = revision.ScriptRevisionId,
        manifest = JsonNode.Parse(revision.Manifest),
        created_by_user_id = revision.CreatedByUserId,
        created_at = revision.CreatedAt
    };

    private static async Task<(User User, Mission Mission)> RequireMission(HttpContext context, RingfourDbContext db,
        Guid missionId, OrgRole role, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        var mission = await db.Missions.SingleOrDefaultAsync(candidate => candidate.Id == missionId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        await IdentityAccess.RequireMembership(db, mission.OrganizationId, user.Id, role, ct);
        return (user, mission);
    }

    private static async Task<IResult> Create(HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        var key = Key(context);
        var document = await Body(context, ct);
        if (document.Count != 2 || document["organization_id"] is not JsonValue orgValue ||
            !orgValue.TryGetValue<string>(out var orgText) || !Guid.TryParseExact(orgText, "D", out var organizationId) ||
            organizationId.ToString() != orgText || document["name"] is not JsonValue nameValue ||
            !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw ApiException.InvalidInput("Invalid mission request.");
        await IdentityAccess.RequireMembership(db, organizationId, user.Id, OrgRole.Developer, ct);
        var fingerprint = Fingerprint(new { organization_id = organizationId, name });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM organizations WHERE id = {organizationId} FOR UPDATE", ct);
        var existing = await db.Missions.SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId &&
            candidate.CreatedByUserId == user.Id && candidate.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
            await transaction.CommitAsync(ct);
            return Results.Ok(Response(existing));
        }
        if (await db.Missions.AnyAsync(candidate => candidate.OrganizationId == organizationId && candidate.Name == name, ct))
            throw ApiException.Conflict("conflict", "A mission with this name already exists.");
        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            CreatedByUserId = user.Id,
            Name = name,
            IdempotencyKey = key,
            RequestFingerprint = fingerprint,
            LatestVersion = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Missions.Add(mission);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(Response(mission), RingfourJson.Options, statusCode: 201);
    }

    private static async Task<IResult> List(HttpContext context, RingfourDbContext db, Guid organizationId, string? cursor, int? limit, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await IdentityAccess.RequireMembership(db, organizationId, user.Id, OrgRole.Operator, ct);
        var (createdAfter, idAfter) = Pagination.DecodeCursor(cursor);
        var pageSize = Pagination.ClampLimit(limit);
        var query = db.Missions.Where(mission => mission.OrganizationId == organizationId).AsQueryable();
        if (createdAfter is not null)
            query = query.Where(mission => mission.CreatedAt > createdAfter || (mission.CreatedAt == createdAfter && mission.Id.CompareTo(idAfter!.Value) > 0));
        var values = await query.OrderBy(mission => mission.CreatedAt).ThenBy(mission => mission.Id).Take(pageSize + 1).ToListAsync(ct);
        var page = values.Take(pageSize).ToList();
        return Results.Ok(new { items = page.Select(Response), next_cursor = Pagination.NextCursor(values.Count > pageSize, page, mission => mission.CreatedAt, mission => mission.Id) });
    }

    private static async Task<IResult> Publish(HttpContext context, RingfourDbContext db, Guid missionId, CancellationToken ct)
    {
        var (user, mission) = await RequireMission(context, db, missionId, OrgRole.Developer, ct);
        var key = Key(context);
        var document = await Body(context, ct);
        try
        {
            if (document.Count != 3 || document["script_revision_id"] is not JsonValue revisionValue ||
                !revisionValue.TryGetValue<string>(out var revisionText) || !Guid.TryParseExact(revisionText, "D", out var scriptId) ||
                scriptId.ToString() != revisionText || document["expected_version"] is not JsonValue versionValue ||
                !versionValue.TryGetValue<int>(out var expected) || expected < 0 || document["manifest"] is not JsonObject manifest)
                throw ApiException.InvalidInput("Invalid mission revision request.");
            if (manifest.Count is < 6 or > 7 || manifest.Any(pair => pair.Key is not
                ("schema_version" or "name" or "entrypoint" or "python_requirement" or "lock_file" or "source_digest" or "robot_profile_ref")) ||
                manifest["schema_version"]?.GetValue<int>() != 1 || manifest["name"]?.GetValue<string>() != mission.Name ||
                manifest["lock_file"]?.GetValue<string>() != "requirements.lock")
                throw ApiException.InvalidInput("Invalid mission manifest.");
            var entrypoint = manifest["entrypoint"]?.GetValue<string>();
            var python = manifest["python_requirement"]?.GetValue<string>();
            var digest = manifest["source_digest"]?.GetValue<string>();
            var profile = manifest["robot_profile_ref"]?.GetValue<string>();
            if (entrypoint is null || python is null || digest is null ||
                (manifest.ContainsKey("robot_profile_ref") && (string.IsNullOrWhiteSpace(profile) || profile.Length > 240)))
                throw ApiException.InvalidInput("Invalid mission manifest.");
            var script = await db.ScriptRevisions.SingleOrDefaultAsync(candidate => candidate.Id == scriptId && candidate.OrganizationId == mission.OrganizationId, ct)
                ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
            if (script.State != "ready" || script.RevisionDigest != digest || script.Entrypoint != entrypoint || script.PythonRequirement != python)
                throw ApiException.InvalidInput("Manifest does not match a finalized source revision.");
            var canonical = new
            {
                schema_version = 1,
                name = mission.Name,
                entrypoint,
                python_requirement = python,
                lock_file = "requirements.lock",
                source_digest = digest,
                robot_profile_ref = profile
            };
            var fingerprint = Fingerprint(new { script_revision_id = scriptId, expected_version = expected, manifest = canonical });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM missions WHERE id = {missionId} FOR UPDATE", ct);
            await db.Entry(mission).ReloadAsync(ct);
            var existing = await db.MissionRevisions.SingleOrDefaultAsync(candidate => candidate.MissionId == missionId &&
                candidate.CreatedByUserId == user.Id && candidate.IdempotencyKey == key, ct);
            if (existing is not null)
            {
                if (existing.RequestFingerprint != fingerprint) throw ApiException.Conflict("conflict", "Idempotency-Key was reused with different input.");
                await transaction.CommitAsync(ct);
                return Results.Ok(Response(existing));
            }
            if (mission.LatestVersion != expected) throw ApiException.Conflict("version_conflict", "Mission version changed; refresh before publishing.");
            var normalized = new JsonObject
            {
                ["schema_version"] = 1,
                ["name"] = mission.Name,
                ["entrypoint"] = entrypoint,
                ["python_requirement"] = python,
                ["lock_file"] = "requirements.lock",
                ["source_digest"] = digest
            };
            if (profile is not null) normalized["robot_profile_ref"] = profile;
            var revision = new MissionRevision
            {
                Id = Guid.NewGuid(),
                MissionId = missionId,
                ScriptRevisionId = scriptId,
                CreatedByUserId = user.Id,
                Version = expected + 1,
                Manifest = normalized.ToJsonString(RingfourJson.Options),
                IdempotencyKey = key,
                RequestFingerprint = fingerprint,
                CreatedAt = DateTimeOffset.UtcNow
            };
            mission.LatestVersion = revision.Version;
            db.MissionRevisions.Add(revision);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Json(Response(revision), RingfourJson.Options, statusCode: 201);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        { throw ApiException.InvalidInput("Invalid mission revision request."); }
    }

    private static async Task<IResult> Revisions(HttpContext context, RingfourDbContext db, Guid missionId, string? cursor, int? limit, CancellationToken ct)
    {
        await RequireMission(context, db, missionId, OrgRole.Operator, ct);
        var afterVersion = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (!value.StartsWith("v1:", StringComparison.Ordinal) || !int.TryParse(value[3..], out afterVersion) || afterVersion < 1)
                    throw new FormatException();
            }
            catch (FormatException) { throw ApiException.InvalidInput("Invalid revision cursor."); }
        }
        var pageSize = Pagination.ClampLimit(limit);
        var values = await db.MissionRevisions.Where(revision => revision.MissionId == missionId && revision.Version > afterVersion)
            .OrderBy(revision => revision.Version).Take(pageSize + 1).ToListAsync(ct);
        var page = values.Take(pageSize).ToList();
        var next = values.Count > pageSize ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"v1:{page[^1].Version}")) : null;
        return Results.Ok(new { items = page.Select(Response), next_cursor = next });
    }
    private static async Task<IResult> Source(HttpContext context, RingfourDbContext db, IObjectStore store,
        Guid missionId, Guid revisionId, CancellationToken ct)
    {
        await RequireMission(context, db, missionId, OrgRole.Operator, ct);
        var source = await db.MissionRevisions.Where(item => item.Id == revisionId && item.MissionId == missionId)
            .Select(item => item.ScriptRevision).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        byte[] archive;
        try { archive = await store.GetAsync($"revisions/{source.Id:N}", ct); }
        catch (Exception exception) when (exception is IOException or AmazonServiceException or AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
        if (archive.Length != source.ArchiveSize || Convert.ToHexStringLower(SHA256.HashData(archive)) != source.ArchiveSha256)
            throw new ApiException(503, "storage_unavailable", "The artifact store returned invalid bytes.");
        return Results.File(archive, "application/zip", "mission-source.zip");
    }

}
