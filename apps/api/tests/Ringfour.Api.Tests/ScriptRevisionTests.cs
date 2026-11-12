using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Ringfour.Api.Persistence;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScriptRevisionTests(PostgresFixture postgres)
{
    internal static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    internal static (byte[] Zip, string BundleDigest, string LockHash) CreateArchive()
    {
        var code = "print('hello')\n"u8.ToArray();
        var lockfile = Array.Empty<byte>();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = zip.CreateEntry("main.py").Open()) stream.Write(code);
            using (var stream = zip.CreateEntry("requirements.lock").Open()) stream.Write(lockfile);
        }
        var manifest = $"[[\"main.py\",\"{Hash(code)}\"],[\"requirements.lock\",\"{Hash(lockfile)}\"]]";
        return (output.ToArray(), Hash(Encoding.UTF8.GetBytes(manifest)), Hash(lockfile));
    }

    [Fact]
    public async Task UploadFinalizeAuthorizationAndIdempotency()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-script-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "script org");
            var (archive, digest, lockHash) = CreateArchive();
            var request = new
            {
                organization_id = org,
                archive_sha256 = Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "same-key");
            var races = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options)));
            Assert.Single(races, result => result.StatusCode == HttpStatusCode.Created);
            Assert.Single(races, result => result.StatusCode == HttpStatusCode.OK);
            var bodies = await Task.WhenAll(races.Select(result => result.Content.ReadAsStringAsync()));
            ContractSchema.AssertValid("scripts/v1/revision.schema.json", bodies[0]);
            var id = JsonDocument.Parse(bodies[0]).RootElement.GetProperty("id").GetGuid();
            Assert.Equal(id, JsonDocument.Parse(bodies[1]).RootElement.GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/v1/script-revisions", request with { entrypoint = "other.py" }, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent(archive))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/v1/script-revisions/{id}/finalize", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent("wrong"u8.ToArray()))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent(archive))).StatusCode);
            var final = await owner.PostAsync($"/v1/script-revisions/{id}/finalize", null);
            Assert.Equal(HttpStatusCode.OK, final.StatusCode);
            var finalBody = await final.Content.ReadAsStringAsync();
            ContractSchema.AssertValid("scripts/v1/revision.schema.json", finalBody);
            Assert.Equal("ready", JsonDocument.Parse(finalBody).RootElement.GetProperty("state").GetString());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/v1/script-revisions/{id}/finalize", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent(archive))).StatusCode);
            Assert.Equal(archive, await File.ReadAllBytesAsync(Path.Combine(root, "revisions", id.ToString("N"))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigratesFromPairingSchemaAndPreservesData()
    {
        var database = $"scripts_{Guid.NewGuid():N}";
        await postgres.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
            var options = new DbContextOptionsBuilder<RingfourDbContext>().UseNpgsql(connection).Options;
            await using var db = new RingfourDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261112130304_DevicePairing");
            var userId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users (id,auth0_sub,created_at) VALUES ({userId},{"auth0|script-migration"},{DateTimeOffset.UtcNow})");
            await migrator.MigrateAsync();
            Assert.True(await db.Users.AnyAsync(user => user.Id == userId));
            Assert.Empty(await db.ScriptRevisions.ToListAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.ExecuteAsync($"DROP DATABASE \"{database}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task ExpiredUploadsAndOperatorWritesAreDenied()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-script-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var operatorClient = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "script permission org");
            var operatorId = await RobotTests.UserId(operatorClient);
            await owner.PostAsJsonAsync($"/v1/organizations/{org}/members", new { user_id = operatorId, role = "operator" }, RingfourJson.Options);
            var (archive, digest, lockHash) = CreateArchive();
            var request = new
            {
                organization_id = org,
                archive_sha256 = Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options)).StatusCode);
            operatorClient.DefaultRequestHeaders.Add("Idempotency-Key", "operator-key");
            Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options)).StatusCode);
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "owner-key");
            var created = await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options);
            var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent(archive))).StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent(new byte[1048577]))).StatusCode);
            await postgres.ExecuteAsync("UPDATE script_revisions SET upload_expires_at=now()-interval '1 second' WHERE id=@id", ("@id", id));
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync($"/v1/script-revisions/{id}/content", new ByteArrayContent(archive))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/v1/script-revisions/{id}/finalize", null)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupRetriesAndPreservesFinalizedBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-script-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "cleanup org");
            var (archive, digest, lockHash) = CreateArchive();
            var request = new
            {
                organization_id = org,
                archive_sha256 = Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "ready");
            var ready = await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options);
            var readyId = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            await owner.PutAsync($"/v1/script-revisions/{readyId}/content", new ByteArrayContent(archive));
            await owner.PostAsync($"/v1/script-revisions/{readyId}/finalize", null);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "expired");
            var expired = await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options);
            var expiredId = JsonDocument.Parse(await expired.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            await owner.PutAsync($"/v1/script-revisions/{expiredId}/content", new ByteArrayContent(archive));
            await postgres.ExecuteAsync("UPDATE script_revisions SET upload_expires_at=now()-interval '25 hours' WHERE id=@id", ("@id", expiredId));
            var options = new DbContextOptionsBuilder<RingfourDbContext>().UseNpgsql(postgres.ConnectionString).Options;
            await using var db = new RingfourDbContext(options);
            await Assert.ThrowsAsync<IOException>(() => ScriptStorageSweeper.SweepOnceAsync(db, new FailingDeleteStore(), DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(await db.ScriptRevisions.AsNoTracking().AnyAsync(item => item.Id == expiredId));
            await ScriptStorageSweeper.SweepOnceAsync(db, new FileObjectStore(root), DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.False(await db.ScriptRevisions.AsNoTracking().AnyAsync(item => item.Id == expiredId));
            Assert.NotNull((await db.ScriptRevisions.AsNoTracking().SingleAsync(item => item.Id == readyId)).UploadCleanedAt);
            Assert.False(File.Exists(Path.Combine(root, "incoming", readyId.ToString("N"))));
            Assert.Equal(archive, await File.ReadAllBytesAsync(Path.Combine(root, "revisions", readyId.ToString("N"))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailingDeleteStore : IObjectStore
    {
        public Task CheckReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PutAsync(string key, byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new IOException("temporary failure");
    }

    [Fact]
    public async Task RevisionQuotaRejectsNewWorkButAllowsIdempotentReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-script-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "quota org");
            var user = await RobotTests.UserId(owner);
            var (archive, digest, lockHash) = CreateArchive();
            var request = new
            {
                organization_id = org,
                archive_sha256 = Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "first");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options)).StatusCode);
            await postgres.ExecuteAsync("""
                INSERT INTO script_revisions (id, organization_id, created_by_user_id, state, archive_sha256,
                  archive_size, revision_digest, entrypoint, python_requirement, lock_sha256, idempotency_key,
                  request_fingerprint, created_at, upload_expires_at)
                SELECT gen_random_uuid(), @org, @user, 'uploading', repeat('a',64), 1, repeat('b',64),
                  'main.py', '>=3.13', repeat('c',64), 'bulk-' || n, repeat('d',64), now(), now()+interval '15 minutes'
                FROM generate_series(1,99) AS n
                """, ("@org", org), ("@user", user));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options)).StatusCode);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "new");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync("/v1/script-revisions", request, RingfourJson.Options)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
