using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

public static class AppArtifactEndpoints
{
    private sealed record Cursor(Guid OrganizationId, Guid AppId, Guid? InstallationId,
        Guid? RobotId, Guid? RunId, string? PathPrefix, DateTimeOffset? PublishedAt, Guid ArtifactId);

    public static void MapAppArtifactEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/apps/{appId:guid}/artifacts", List)
            .RequireAuthorization().WithMetadata(new AppErrorResponse());

    private static string FilterDigest(Guid organizationId, Guid appId, Guid? installationId,
        Guid? robotId, Guid? runId, string? pathPrefix)
    {
        var filters = new JsonObject
        {
            ["organization_id"] = organizationId.ToString(),
            ["app_id"] = appId.ToString(),
            ["installation_id"] = installationId?.ToString(),
            ["robot_id"] = robotId?.ToString(),
            ["run_id"] = runId?.ToString(),
            ["path_prefix"] = pathPrefix
        };
        return AppProtocol.Digest(filters);
    }

    private static string? NormalizePrefix(string? prefix)
    {
        if (prefix is null) return null;
        if (prefix.Length is 0 or > 240 || prefix.StartsWith('/') || prefix.Contains('\\') || prefix.Any(char.IsControl))
            throw ApiException.InvalidInput("path_prefix must be a safe relative App artifact path prefix.");
        var parts = prefix.Split('/');
        var count = parts.Length - (prefix.EndsWith('/') ? 1 : 0);
        if (count == 0 || parts.Take(count).Any(part => part.Length == 0 || part is "." or ".."))
            throw ApiException.InvalidInput("path_prefix must be a safe relative App artifact path prefix.");
        return prefix;
    }

    private static Cursor? DecodeCursor(string? value, Guid organizationId, Guid appId,
        Guid? installationId, Guid? robotId, Guid? runId, string? pathPrefix)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 1024) throw ApiException.InvalidInput("cursor is too long.");
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var root = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)))!.AsObject();
            if (root.Count != 3 || root["filter_digest"]?.GetValue<string>() !=
                FilterDigest(organizationId, appId, installationId, robotId, runId, pathPrefix))
                throw new FormatException();
            DateTimeOffset? timestamp = root["published_at"] is null
                ? null
                : root["published_at"]!.GetValue<DateTimeOffset>();
            var artifactId = Guid.Parse(root["artifact_id"]!.GetValue<string>());
            return new Cursor(organizationId, appId, installationId, robotId, runId, pathPrefix,
                timestamp, artifactId);
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or
                                      System.Text.Json.JsonException or ArgumentException)
        {
            throw ApiException.InvalidInput("cursor is invalid or does not match these filters.");
        }
    }

    private static string EncodeCursor(AppArtifact artifact, string filterDigest)
    {
        var value = new JsonObject
        {
            ["filter_digest"] = filterDigest,
            ["published_at"] = artifact.PublishedAt?.ToUniversalTime().ToString("O"),
            ["artifact_id"] = artifact.Id.ToString()
        };
        return Convert.ToBase64String(AppProtocol.Canonical(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task<IResult> List(Guid appId,
        [FromQuery(Name = "organization_id")] Guid organization_id,
        Guid? installation_id, Guid? robot_id, Guid? run_id, string? path_prefix,
        string? cursor, int? limit, HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await IdentityAccess.RequireMembership(db, organization_id, user.Id, OrgRole.Operator, ct);
        if (limit is < 1 or > 100) throw ApiException.InvalidInput("limit must be between 1 and 100.");
        path_prefix = NormalizePrefix(path_prefix);

        var visible = await db.AppImports.AsNoTracking().AnyAsync(
            x => x.OrganizationId == organization_id && x.AppId == appId, ct)
            || await db.AppInstallations.AsNoTracking().AnyAsync(
                x => x.OrganizationId == organization_id && x.AppId == appId, ct);
        if (!visible) throw ApiException.NotFound("App is not accessible in this workspace.");

        if (installation_id is not null && !await db.AppInstallations.AsNoTracking().AnyAsync(
                x => x.Id == installation_id && x.OrganizationId == organization_id && x.AppId == appId, ct))
            throw ApiException.NotFound("Installation is not accessible for this App in this workspace.");
        if (robot_id is not null && !await db.AppInstallations.AsNoTracking().AnyAsync(
                x => x.OrganizationId == organization_id && x.AppId == appId && x.RobotId == robot_id, ct))
            throw ApiException.NotFound("Robot has no installation for this App in this workspace.");
        if (run_id is not null && !await db.AppRuns.AsNoTracking().AnyAsync(
                x => x.Id == run_id && x.OrganizationId == organization_id &&
                     db.AppInstallations.Any(i => i.Id == x.InstallationId && i.AppId == appId) &&
                     (installation_id == null || x.InstallationId == installation_id) &&
                     (robot_id == null || x.RobotId == robot_id), ct))
            throw ApiException.NotFound("Run is not accessible for this App in this workspace.");

        var decoded = DecodeCursor(cursor, organization_id, appId, installation_id, robot_id, run_id, path_prefix);
        var filterDigest = FilterDigest(organization_id, appId, installation_id, robot_id, run_id, path_prefix);
        var query = from artifact in db.AppArtifacts.AsNoTracking()
                    join run in db.AppRuns.AsNoTracking() on artifact.RunId equals run.Id
                    join installation in db.AppInstallations.AsNoTracking() on run.InstallationId equals installation.Id
                    where run.OrganizationId == organization_id && installation.AppId == appId
                    select new { Artifact = artifact, Run = run, Installation = installation };
        if (installation_id is not null) query = query.Where(x => x.Installation.Id == installation_id);
        if (robot_id is not null) query = query.Where(x => x.Run.RobotId == robot_id);
        if (run_id is not null) query = query.Where(x => x.Run.Id == run_id);
        if (path_prefix is not null) query = query.Where(x => x.Artifact.Path.StartsWith(path_prefix));
        if (decoded is not null)
        {
            var at = decoded.PublishedAt;
            var id = decoded.ArtifactId;
            query = at is null
                ? query.Where(x => x.Artifact.PublishedAt == null && x.Artifact.Id.CompareTo(id) > 0)
                : query.Where(x => x.Artifact.PublishedAt == null || x.Artifact.PublishedAt < at ||
                    (x.Artifact.PublishedAt == at && x.Artifact.Id.CompareTo(id) > 0));
        }

        var size = limit ?? 50;
        var rows = await query.OrderByDescending(x => x.Artifact.PublishedAt.HasValue)
            .ThenByDescending(x => x.Artifact.PublishedAt).ThenBy(x => x.Artifact.Id)
            .Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();
        var items = page.Select(x => new
        {
            artifact_id = x.Artifact.Id,
            run_id = x.Run.Id,
            installation_id = x.Installation.Id,
            robot_id = x.Run.RobotId,
            path = x.Artifact.Path,
            size = x.Artifact.Size,
            sha256 = x.Artifact.Sha256,
            content_type = x.Artifact.ContentType,
            published_at = AppInstallationEndpoints.Stamp(x.Artifact.PublishedAt)
        });
        var next = rows.Count <= size || page.Count == 0
            ? null
            : EncodeCursor(page[^1].Artifact, filterDigest);
        return Results.Json(new { items, next_cursor = next });
    }
}
