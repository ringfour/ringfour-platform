using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Logbook;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Storage;

public static class DeviceExecutionEndpoints
{
    public static void MapDeviceExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/device/executions");
        group.MapGet("/{executionId:guid}/revision", Revision);
        group.MapPut("/{executionId:guid}/output/{stream}", Output);
        group.MapPut("/{executionId:guid}/artifacts", Artifact);
        group.MapPut("/{executionId:guid}/artifact-status", ArtifactStatus);
        group.MapPut("/{executionId:guid}/status", Status);
    }

    private static async Task<ScriptExecution> Authenticate(HttpContext context, RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var secret = context.Request.Headers["x-ringfour-credential-secret"].ToString();
        if (string.IsNullOrWhiteSpace(secret)) throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, _) = await DeviceAuth.VerifyCredential(db, credentialId, secret, ct);
        return await db.ScriptExecutions.SingleOrDefaultAsync(candidate => candidate.Id == executionId && candidate.RobotId == robot.Id, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist for this robot.");
    }

    private static async Task Lock(RingfourDbContext db, ScriptExecution execution, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM script_executions WHERE id = {execution.Id} FOR UPDATE", ct);
        await db.Entry(execution).ReloadAsync(ct);
    }

    private static async Task<IResult> Revision(HttpContext context, RingfourDbContext db, IObjectStore store, Guid executionId, CancellationToken ct)
    {
        var execution = await Authenticate(context, db, executionId, ct);
        if (ScriptRunEndpoints.Terminal(execution.State))
            throw ApiException.Conflict("conflict", "Execution is terminal.");
        var revision = await db.ScriptRevisions.SingleAsync(candidate => candidate.Id == execution.RevisionId, ct);
        return await TemporaryTransfer.VerifiedResponseAsync(context, store, $"revisions/{revision.Id:N}",
            revision.ArchiveSize, revision.ArchiveSha256, "application/zip", null, ct);
    }

    private static async Task<IResult> Output(HttpContext context, RingfourDbContext db, Guid executionId, string stream, int offset, CancellationToken ct)
    {
        var execution = await Authenticate(context, db, executionId, ct);
        if (stream is not ("stdout" or "stderr") || offset is < 0 or > 65536)
            throw ApiException.InvalidInput("Invalid output stream or offset.");
        var data = await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 16384, ct);
        if (offset + data.Length > 65536) throw new ApiException(413, "size_limit", "Output exceeds the byte limit.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, execution, ct);
        var prior = stream == "stdout" ? execution.Stdout : execution.Stderr;
        if (offset < prior.Length)
        {
            if (offset + data.Length > prior.Length || !prior.AsSpan(offset, data.Length).SequenceEqual(data))
                throw ApiException.Conflict("conflict", "Output replay conflicts with stored bytes.");
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        if (offset != prior.Length || (data.Length > 0 && ScriptRunEndpoints.Terminal(execution.State)))
            throw ApiException.Conflict("conflict", "Output offset is invalid or execution is terminal.");
        if (data.Length > 0)
        {
            var combined = new byte[prior.Length + data.Length];
            prior.CopyTo(combined, 0);
            data.CopyTo(combined, prior.Length);
            if (stream == "stdout") execution.Stdout = combined; else execution.Stderr = combined;
            execution.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static bool ValidArtifactPath(string path) => !string.IsNullOrEmpty(path) && Encoding.UTF8.GetByteCount(path) <= 240 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Any(char.IsControl) &&
        path.Split('/').All(part => !string.IsNullOrEmpty(part) && part is not ("." or ".."));

    private static async Task<IResult> Artifact(HttpContext context, RingfourDbContext db, IObjectStore store,
        Guid executionId, string path, string sha256, CancellationToken ct)
    {
        var execution = await Authenticate(context, db, executionId, ct);
        var artifactIdText = context.Request.Query["artifact_id"].ToString();
        var contentType = context.Request.Query["content_type"].ToString();
        var hasArtifactId = !string.IsNullOrEmpty(artifactIdText);
        if (!hasArtifactId) contentType = "application/octet-stream";
        if (!ValidArtifactPath(path) || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            (hasArtifactId && (!Guid.TryParseExact(artifactIdText, "D", out _) || Guid.Parse(artifactIdText).ToString() != artifactIdText)) ||
            contentType is not ("image/jpeg" or "image/png" or "application/octet-stream"))
            throw ApiException.InvalidInput("Invalid artifact path or digest.");
        var artifactId = hasArtifactId ? Guid.Parse(artifactIdText) : Guid.NewGuid();
        var maxBody = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = null;
        TemporaryTransfer temporary;
        try { temporary = await TemporaryTransfer.FromRequestAsync(context.Request.Body, 9007199254740991, ct); }
        catch (InvalidDataException) { throw new ApiException(413, "size_limit", "Request body exceeds its byte limit."); }
        catch (InsufficientTransferStorageException) { throw new ApiException(503, "insufficient_storage", "Not enough temporary storage for this artifact."); }
        catch (IOException) { throw new ApiException(503, "storage_unavailable", "Unable to stage the artifact."); }
        await using var data = temporary;
        if (data.Sha256 != sha256) throw ApiException.InvalidInput("Artifact digest mismatch.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, execution, ct);
        var existing = await db.ScriptArtifacts.SingleOrDefaultAsync(candidate => candidate.ExecutionId == executionId && candidate.Path == path, ct);
        if (existing is not null)
        {
            if (existing.Sha256 != sha256 || existing.Size != data.Length || (hasArtifactId && (existing.Id != artifactId || existing.ContentType != contentType)))
                throw ApiException.Conflict("conflict", "Artifact replay conflicts with stored bytes.");
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        if (hasArtifactId && await db.ScriptArtifacts.AnyAsync(candidate => candidate.Id == artifactId, ct))
            throw ApiException.Conflict("conflict", "Artifact ID belongs to another publication.");
        if (execution.ArtifactState == "ready") throw ApiException.Conflict("conflict", "Artifact delivery is already complete.");
        var current = await db.ScriptArtifacts.Where(candidate => candidate.ExecutionId == executionId).ToListAsync(ct);
        if (current.Count >= 16)
            throw new ApiException(413, "size_limit", "Artifact retention capacity reached.");
        try { await store.PutFileAsync(ScriptRunEndpoints.ArtifactKey(executionId, path), data.Path, ct); }
        catch (InsufficientTransferStorageException) { throw new ApiException(503, "insufficient_storage", "Not enough object storage for this artifact."); }
        catch (Exception exception) when (exception is IOException or AmazonServiceException or AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
        db.ScriptArtifacts.Add(new ScriptArtifact { Id = artifactId, ExecutionId = executionId, Path = path, Size = data.Length,
            Sha256 = sha256, ContentType = contentType, State = "available" });
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            try { await store.DeleteAsync(ScriptRunEndpoints.ArtifactKey(executionId, path), CancellationToken.None); }
            catch { }
            throw;
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ArtifactStatus(HttpContext context, RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        var execution = await Authenticate(context, db, executionId, ct);
        JsonObject? payload;
        string? state;
        JsonObject? failure;
        try
        {
            payload = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject;
            if (payload is null || payload.Count != 2 || payload.Any(pair => pair.Key is not ("state" or "error"))) throw new JsonException();
            state = payload["state"]?.GetValue<string>();
            failure = payload["error"] as JsonObject;
            if (payload["error"] is not null && failure is null) throw new JsonException();
            if (state is not ("uploading" or "ready" or "failed") || (state == "failed") != (failure is not null)) throw new JsonException();
            if (failure is not null && (failure.Count != 2 || failure.Any(pair => pair.Key is not ("code" or "message")) ||
                failure["code"]?.GetValue<string>() is not { Length: > 0 and <= 100 } ||
                failure["message"]?.GetValue<string>() is not { Length: > 0 and <= 500 })) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { throw ApiException.InvalidInput("Invalid artifact delivery status."); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, execution, ct);
        if (!ScriptRunEndpoints.Terminal(execution.State)) throw ApiException.Conflict("conflict", "Execution is not terminal.");
        if (execution.ArtifactState == "ready")
        {
            if (state == "failed") throw ApiException.Conflict("conflict", "Artifact delivery is already complete.");
            await transaction.CommitAsync(ct);
            return Results.NoContent(); // Retry after the ready response was lost.
        }
        execution.ArtifactState = state;
        execution.ArtifactError = failure?.ToJsonString(RingfourJson.Options);
        execution.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static int Rank(string state) => state switch { "queued" => 0, "preparing" => 1, "running" => 2, _ => 3 };
    private static async Task<IResult> Status(HttpContext context, RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        var execution = await Authenticate(context, db, executionId, ct);
        var data = await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct);
        JsonObject? payload;
        string? state;
        int? exitCode;
        JsonObject? failure;
        bool stdoutTruncated;
        bool stderrTruncated;
        try
        {
            payload = JsonNode.Parse(data) as JsonObject;
            if (payload is null || payload.Count != 5 || payload.Any(pair => pair.Key is not
                ("state" or "exit_code" or "error" or "stdout_truncated" or "stderr_truncated"))) throw new JsonException();
            state = payload["state"]?.GetValue<string>();
            exitCode = payload["exit_code"]?.GetValue<int>();
            failure = payload["error"] as JsonObject;
            if (payload["error"] is not null && failure is null) throw new JsonException();
            stdoutTruncated = payload["stdout_truncated"]?.GetValue<bool>() ?? throw new JsonException();
            stderrTruncated = payload["stderr_truncated"]?.GetValue<bool>() ?? throw new JsonException();
            if (failure is not null && (failure.Count != 2 || failure.Any(pair => pair.Key is not ("code" or "message")) ||
                failure["code"]?.GetValue<string>() is not { Length: <= 100 } || failure["message"]?.GetValue<string>() is not { Length: <= 500 }))
                throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { throw ApiException.InvalidInput("Invalid execution status."); }
        if (state is not ("queued" or "preparing" or "running" or "succeeded" or "failed" or "cancelled") ||
            (!ScriptRunEndpoints.Terminal(state) && (exitCode is not null || failure is not null)) ||
            (state == "succeeded" && (exitCode != 0 || failure is not null)) ||
            (state == "failed" && failure is null))
            throw ApiException.InvalidInput("Invalid execution status.");
        var failureJson = failure?.ToJsonString(RingfourJson.Options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, execution, ct);
        if (ScriptRunEndpoints.Terminal(execution.State))
        {
            if (execution.State != state || execution.ExitCode != exitCode || !JsonNode.DeepEquals(
                execution.Error is null ? null : JsonNode.Parse(execution.Error), failure) ||
                execution.StdoutTruncated != stdoutTruncated || execution.StderrTruncated != stderrTruncated)
                throw ApiException.Conflict("conflict", "Terminal execution state is immutable.");
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        if (Rank(state) < Rank(execution.State)) throw ApiException.Conflict("conflict", "Execution state cannot move backwards.");
        if (execution.State != state)
        {
            var occurredAt = DateTimeOffset.UtcNow;
            var lifecycleEventId = Guid.NewGuid();
            db.ScriptExecutionEvents.Add(new ScriptExecutionEvent
            {
                Id = lifecycleEventId,
                ExecutionId = execution.Id,
                ActorUserId = execution.ActorUserId,
                State = state,
                OccurredAt = occurredAt
            });
            MissionLifecycleLogbook.Record(db, execution, lifecycleEventId, state, occurredAt);
        }
        execution.State = state;
        execution.ExitCode = exitCode;
        execution.Error = failureJson;
        execution.StdoutTruncated = stdoutTruncated;
        execution.StderrTruncated = stderrTruncated;
        execution.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}
