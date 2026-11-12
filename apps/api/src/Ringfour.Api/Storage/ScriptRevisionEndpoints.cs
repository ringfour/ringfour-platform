using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Storage;

public static class ScriptRevisionEndpoints
{
    private const int MaxBody = 4096;

    public static void MapScriptRevisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/script-revisions").RequireAuthorization();
        group.MapPost("", Create);
        group.MapPut("/{revisionId:guid}/content", Upload);
        group.MapPost("/{revisionId:guid}/finalize", FinalizeRevision);
    }

    private sealed record CreateRevisionRequest(Guid OrganizationId, string ArchiveSha256, int ArchiveSize,
        string RevisionDigest, string Entrypoint, string PythonRequirement, string LockSha256);

    private sealed record RevisionResponse(Guid Id, Guid OrganizationId, Guid CreatedByUserId, string State,
        string ArchiveSha256, int ArchiveSize, string RevisionDigest, string Entrypoint,
        string PythonRequirement, string LockSha256, string UploadUrl, DateTimeOffset UploadExpiresAt,
        DateTimeOffset CreatedAt, DateTimeOffset? FinalizedAt)
    {
        public static RevisionResponse From(ScriptRevision revision) => new(revision.Id, revision.OrganizationId,
            revision.CreatedByUserId, revision.State, revision.ArchiveSha256, revision.ArchiveSize,
            revision.RevisionDigest, revision.Entrypoint, revision.PythonRequirement, revision.LockSha256,
            $"/v1/script-revisions/{revision.Id}/content", revision.UploadExpiresAt, revision.CreatedAt, revision.FinalizedAt);
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static bool ValidHash(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$");
    private static bool ValidEntry(string? path) => path is { Length: >= 4 and <= 240 } &&
        path.EndsWith(".py", StringComparison.Ordinal) && !path.StartsWith('/') &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));

    private static async Task<IResult> Create(HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw ApiException.InvalidInput("Idempotency-Key is required and must not exceed 200 characters.");
        var buffer = await ReadLimited(context.Request.Body, MaxBody, ct);
        CreateRevisionRequest? request;
        try
        {
            var document = JsonNode.Parse(buffer) as JsonObject;
            if (document is null || document.Count != 7 || document.Any(pair => pair.Key is not
                ("organization_id" or "archive_sha256" or "archive_size" or "revision_digest" or "entrypoint" or "python_requirement" or "lock_sha256")))
                throw new JsonException();
            request = JsonSerializer.Deserialize<CreateRevisionRequest>(buffer, RingfourJson.Options);
            if (request is null || document["organization_id"]?.ToString() != request.OrganizationId.ToString())
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw ApiException.InvalidInput("Invalid revision request.");
        }
        if (request is null || request.OrganizationId == Guid.Empty || !ValidHash(request.ArchiveSha256) ||
            request.ArchiveSize is < 1 or > ObjectStoreKey.MaxObjectBytes || !ValidHash(request.RevisionDigest) ||
            !ValidEntry(request.Entrypoint) || string.IsNullOrWhiteSpace(request.PythonRequirement) ||
            request.PythonRequirement.Length > 128 || !ValidHash(request.LockSha256))
            throw ApiException.InvalidInput("Invalid revision request.");
        await IdentityAccess.RequireMembership(db, request.OrganizationId, user.Id, OrgRole.Developer, ct);
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(request, RingfourJson.Options));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM organizations WHERE id = {request.OrganizationId} FOR UPDATE", ct);
        var existing = await db.ScriptRevisions.SingleOrDefaultAsync(candidate => candidate.OrganizationId == request.OrganizationId &&
            candidate.CreatedByUserId == user.Id && candidate.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint)
                throw ApiException.Conflict("conflict", "Idempotency-Key was already used with different input.");
            await transaction.CommitAsync(ct);
            return Results.Ok(RevisionResponse.From(existing));
        }
        if (await db.ScriptRevisions.CountAsync(candidate => candidate.OrganizationId == request.OrganizationId, ct) >= 100)
            throw new ApiException(503, "capacity_exceeded", "Revision retention capacity reached.");
        var now = DateTimeOffset.UtcNow;
        var revision = new ScriptRevision
        {
            Id = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            CreatedByUserId = user.Id,
            State = "uploading",
            ArchiveSha256 = request.ArchiveSha256,
            ArchiveSize = request.ArchiveSize,
            RevisionDigest = request.RevisionDigest,
            Entrypoint = request.Entrypoint,
            PythonRequirement = request.PythonRequirement,
            LockSha256 = request.LockSha256,
            IdempotencyKey = key,
            RequestFingerprint = fingerprint,
            CreatedAt = now,
            UploadExpiresAt = now.AddMinutes(15),
        };
        db.ScriptRevisions.Add(revision);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(RevisionResponse.From(revision), RingfourJson.Options, statusCode: 201);
    }

    private static async Task<ScriptRevision> Authorize(HttpContext context, RingfourDbContext db, Guid id, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        var revision = await db.ScriptRevisions.SingleOrDefaultAsync(candidate => candidate.Id == id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        await IdentityAccess.RequireMembership(db, revision.OrganizationId, user.Id, OrgRole.Developer, ct);
        return revision;
    }

    private static async Task Lock(RingfourDbContext db, ScriptRevision revision, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM script_revisions WHERE id = {revision.Id} FOR UPDATE", ct);
        await db.Entry(revision).ReloadAsync(ct);
    }

    private static async Task<IResult> Upload(HttpContext context, RingfourDbContext db, IObjectStore store, Guid revisionId, CancellationToken ct)
    {
        var revision = await Authorize(context, db, revisionId, ct);
        var data = await ReadLimited(context.Request.Body, ObjectStoreKey.MaxObjectBytes, ct);
        if (data.Length != revision.ArchiveSize || Hash(data) != revision.ArchiveSha256)
            throw ApiException.InvalidInput("Uploaded bytes do not match the declared size and digest.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, revision, ct);
        if (revision.State != "uploading" || revision.UploadExpiresAt <= DateTimeOffset.UtcNow)
            throw ApiException.Conflict("conflict", "The revision is finalized or upload has expired.");
        await StorePut(store, $"incoming/{revision.Id:N}", data, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> FinalizeRevision(HttpContext context, RingfourDbContext db, IObjectStore store, Guid revisionId, CancellationToken ct)
    {
        var revision = await Authorize(context, db, revisionId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, revision, ct);
        if (revision.State == "ready")
        {
            await transaction.CommitAsync(ct);
            return Results.Ok(RevisionResponse.From(revision));
        }
        if (revision.UploadExpiresAt <= DateTimeOffset.UtcNow)
            throw ApiException.Conflict("conflict", "The upload has expired.");
        byte[] archive;
        try { archive = await store.GetAsync($"incoming/{revision.Id:N}", ct); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException || exception is AmazonS3Exception { StatusCode: HttpStatusCode.NotFound })
        { throw ApiException.Conflict("conflict", "No uploaded archive is available."); }
        catch (Exception exception) when (exception is IOException or AmazonServiceException or AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
        try
        {
            await ScriptArchive.VerifyAsync(archive, revision.ArchiveSha256, revision.ArchiveSize,
                revision.RevisionDigest, revision.Entrypoint, revision.LockSha256, ct);
        }
        catch (InvalidDataException)
        { throw ApiException.InvalidInput("The uploaded archive failed validation."); }
        await StorePut(store, $"revisions/{revision.Id:N}", archive, ct);
        revision.State = "ready";
        revision.FinalizedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(RevisionResponse.From(revision));
    }

    private static async Task StorePut(IObjectStore store, string key, byte[] data, CancellationToken ct)
    {
        try { await store.PutAsync(key, data, ct); }
        catch (Exception exception) when (exception is IOException or AmazonServiceException or AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
    }

    internal static async Task<byte[]> ReadLimited(Stream stream, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (output.Length + read > limit) throw new ApiException(413, "size_limit", "Request body exceeds its byte limit.");
            await output.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
}
