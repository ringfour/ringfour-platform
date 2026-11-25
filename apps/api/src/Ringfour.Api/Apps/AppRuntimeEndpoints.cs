using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Apps;

public static class AppRuntimeEndpoints
{
    public static void MapAppRuntimeEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/v1").WithMetadata(new AppErrorResponse());
        g.MapPut("/device/app-runs/{runId:guid}/output/{stream}", UploadOutput);
        g.MapPut("/device/app-runs/{runId:guid}/artifacts/{artifactId:guid}", UploadArtifact);
        g.MapGet("/app-runs/{runId:guid}/output", Output).RequireAuthorization();
        g.MapGet("/app-runs/{runId:guid}/artifacts", Artifacts).RequireAuthorization();
        g.MapGet("/app-runs/{runId:guid}/artifacts/{artifactId:guid}", Artifact).RequireAuthorization();
        g.MapGet("/app-runs/{runId:guid}/artifacts/{artifactId:guid}/content", Content).RequireAuthorization();
    }
    private static async Task<AppRun> Machine(HttpContext c, RingfourDbContext db, Guid runId, CancellationToken ct)
    {
        var robot = await AppInventoryEndpoints.Authenticate(c, db, ct);
        return await db.AppRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId && x.RobotId == robot.Id, ct) ?? throw ApiException.NotFound("Run does not belong to this robot.");
    }
    private static async Task Lock(HttpContext c, RingfourDbContext db, AppRun run, CancellationToken ct)
    {
        await db.Robots.FromSqlInterpolated($"SELECT * FROM robots WHERE id = {run.RobotId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
        var credentialId=Guid.Parse(c.Request.Headers["x-ringfour-credential-id"].ToString());
        var credential=await db.RobotCredentials.FromSqlInterpolated($"SELECT * FROM robot_credentials WHERE id = {credentialId} FOR UPDATE").SingleAsync(ct);
        await db.Entry(credential).ReloadAsync(ct);
        await AppInventoryEndpoints.Authenticate(c, db, ct);
    }
    private static async Task<IResult> UploadOutput(Guid runId, string stream, int offset, bool sealed_output, bool truncated, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        var run = await Machine(c, db, runId, ct);
        if (stream is not ("stdout" or "stderr") || offset is < 0 or > 65536) throw ApiException.InvalidInput("Invalid stream/offset.");
        var bytes = await RequestBody.ReadLimited(c.Request.Body, 16384, ct);
        if (offset + bytes.Length > 65536) throw ApiException.InvalidInput("Output limit exceeded.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(c, db, run, ct);
        var output = await db.AppRunOutputs.SingleOrDefaultAsync(x => x.RunId == runId, ct);
        if (output is null) { output = new AppRunOutput { RunId = runId }; db.AppRunOutputs.Add(output); }
        var prior = stream == "stdout" ? output.Stdout : output.Stderr; var sealedBefore = stream == "stdout" ? output.StdoutSealed : output.StderrSealed;
        if (offset > prior.Length || (offset < prior.Length && (offset + bytes.Length > prior.Length || !prior.AsSpan(offset,bytes.Length).SequenceEqual(bytes))) || (sealedBefore && offset + bytes.Length > prior.Length))
            throw ApiException.Conflict("invalid_observation", "Output offset/content conflicts with durable bytes.");
        if (offset == prior.Length && bytes.Length > 0) prior = prior.Concat(bytes).ToArray();
        if (sealed_output && offset + bytes.Length != prior.Length) throw ApiException.Conflict("invalid_observation", "Seal must acknowledge the complete prefix.");
        if (stream == "stdout") { output.Stdout = prior; output.StdoutSealed |= sealed_output; output.StdoutTruncated |= truncated; }
        else { output.Stderr = prior; output.StderrSealed |= sealed_output; output.StderrTruncated |= truncated; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.NoContent();
    }
    private static async Task<IResult> Output(Guid runId, string stream, int? offset, HttpContext c, RingfourDbContext db, CancellationToken ct)
    {
        await AppRunEndpoints.Read(runId,c,db,ct); var at = offset ?? 0;
        if (stream is not ("stdout" or "stderr") || at is < 0 or > 65536) throw ApiException.InvalidInput("Invalid stream/offset.");
        var output = await db.AppRunOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId, ct) ?? new AppRunOutput();
        var bytes = stream == "stdout" ? output.Stdout : output.Stderr;
        if (at > bytes.Length) throw ApiException.InvalidInput("Offset exceeds delivered prefix.");
        var data = bytes.Skip(at).Take(16384).ToArray();
        return Results.Json(new { run_id=runId, stream, offset=at, next_offset=at+data.Length, data_base64=Convert.ToBase64String(data), @sealed=stream=="stdout"?output.StdoutSealed:output.StderrSealed, truncated=stream=="stdout"?output.StdoutTruncated:output.StderrTruncated });
    }
    private static string Key(Guid run, Guid artifact) => $"app-runs/{run:N}/artifacts/{artifact:N}";
    private static bool ValidPath(string path) => path.Length is > 0 and <= 240 && !path.StartsWith('/') && !path.Contains('\\') && !path.Any(char.IsControl) && path.Split('/').All(p => p.Length > 0 && p is not ("." or ".."));
    private static async Task<IResult> UploadArtifact(Guid runId, Guid artifactId, string path, string sha256, string content_type, HttpContext c, RingfourDbContext db, IObjectStore store, CancellationToken ct)
    {
        var run = await Machine(c,db,runId,ct);
        if (!ValidPath(path) || sha256.Length != 64 || sha256.Any(x => !char.IsAsciiHexDigitLower(x)) || content_type is not ("image/jpeg" or "image/png" or "application/octet-stream")) throw ApiException.InvalidInput("Invalid artifact metadata.");
        TemporaryTransfer temporary;
        try { temporary = await TemporaryTransfer.FromRequestAsync(c.Request.Body,16777216,ct); }
        catch (InvalidDataException) { throw new ApiException(413,"invalid_input","Artifact exceeds 16 MiB."); }
        await using var data = temporary;
        if (data.Sha256 != sha256) throw ApiException.InvalidInput("Artifact digest mismatch.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(c,db,run,ct);
        var existing = await db.AppArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId || (x.RunId == runId && x.Path == path),ct);
        if (existing is not null)
        {
            if (existing.Id != artifactId || existing.RunId != runId || existing.Path != path || existing.Sha256 != sha256 || existing.Size != data.Length || existing.ContentType != content_type) throw ApiException.Conflict("invalid_observation","Artifact replay conflicts.");
            return Results.NoContent();
        }
        if (await db.AppArtifacts.CountAsync(x => x.RunId == runId,ct) >= 16) throw new ApiException(413,"storage_unavailable","Artifact retention capacity reached.");
        await store.PutFileAsync(Key(runId,artifactId),data.Path,ct);
        db.AppArtifacts.Add(new AppArtifact { Id=artifactId,RunId=runId,Path=path,Size=data.Length,Sha256=sha256,ContentType=content_type,PublishedAt=DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.NoContent();
    }
    internal static object ArtifactJson(AppArtifact a) => new { artifact_id=a.Id,run_id=a.RunId,path=a.Path,size=a.Size,sha256=a.Sha256,content_type=a.ContentType,state="available" };
    private static async Task<IResult> Artifacts(Guid runId,HttpContext c,RingfourDbContext db,CancellationToken ct)
    { await AppRunEndpoints.Read(runId,c,db,ct);return Results.Json(new { items=(await db.AppArtifacts.AsNoTracking().Where(x=>x.RunId==runId).OrderBy(x=>x.Path).ToListAsync(ct)).Select(ArtifactJson) }); }
    private static async Task<AppArtifact> ReadArtifact(Guid runId,Guid artifactId,HttpContext c,RingfourDbContext db,CancellationToken ct)
    { await AppRunEndpoints.Read(runId,c,db,ct);return await db.AppArtifacts.AsNoTracking().SingleOrDefaultAsync(x=>x.RunId==runId&&x.Id==artifactId,ct)??throw ApiException.NotFound("Artifact is not available for this run."); }
    private static async Task<IResult> Artifact(Guid runId,Guid artifactId,HttpContext c,RingfourDbContext db,CancellationToken ct) => Results.Json(ArtifactJson(await ReadArtifact(runId,artifactId,c,db,ct)));
    private static async Task<IResult> Content(Guid runId,Guid artifactId,HttpContext c,RingfourDbContext db,IObjectStore store,CancellationToken ct)
    { var artifact=await ReadArtifact(runId,artifactId,c,db,ct);return await TemporaryTransfer.VerifiedResponseAsync(c,store,Key(runId,artifactId),artifact.Size,artifact.Sha256,artifact.ContentType,artifact.Path.Split('/').Last(),ct); }
}
