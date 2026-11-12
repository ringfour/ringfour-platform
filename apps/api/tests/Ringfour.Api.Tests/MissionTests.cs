using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MissionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MissionPublishingIsScopedVersionedAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-mission-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new ApiFactory(postgres.ConnectionString, storageRoot: root);
            var owner = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var outsider = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
            var org = await OrganizationTests.CreateOrganization(owner, "mission org");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "create-mission");
            var create = new { organization_id = org, name = "hello" };
            var first = await owner.PostAsJsonAsync("/v1/missions", create, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var missionJson = await first.Content.ReadAsStringAsync();
            ContractSchema.AssertValid("missions/v1/mission.schema.json", missionJson);
            var missionId = JsonDocument.Parse(missionJson).RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/v1/missions", create, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/v1/missions", create with { name = "other" }, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/organizations/{org}/missions")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/missions/{missionId}/revisions")).StatusCode);
            var (archive, digest, lockHash) = ScriptRevisionTests.CreateArchive();
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "source");
            var source = new
            {
                organization_id = org,
                archive_sha256 = ScriptRevisionTests.Hash(archive),
                archive_size = archive.Length,
                revision_digest = digest,
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_sha256 = lockHash
            };
            var sourceResponse = await owner.PostAsJsonAsync("/v1/script-revisions", source, RingfourJson.Options);
            var sourceId = JsonDocument.Parse(await sourceResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
            await owner.PutAsync($"/v1/script-revisions/{sourceId}/content", new ByteArrayContent(archive));
            await owner.PostAsync($"/v1/script-revisions/{sourceId}/finalize", null);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "publish-v1");
            var manifest = new
            {
                schema_version = 1,
                name = "hello",
                entrypoint = "main.py",
                python_requirement = ">=3.13,<3.14",
                lock_file = "requirements.lock",
                source_digest = digest
            };
            var publish = new { script_revision_id = sourceId, expected_version = 0, manifest };
            var published = await owner.PostAsJsonAsync($"/v1/missions/{missionId}/revisions", publish, RingfourJson.Options);
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
            var revisionJson = await published.Content.ReadAsStringAsync();
            ContractSchema.AssertValid("missions/v1/revision.schema.json", revisionJson);
            var revision = JsonDocument.Parse(revisionJson).RootElement;
            ContractSchema.AssertValid("missions/v1/manifest.schema.json", revision.GetProperty("manifest").GetRawText());
            Assert.Equal(1, revision.GetProperty("version").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/v1/missions/{missionId}/revisions", publish, RingfourJson.Options)).StatusCode);
            owner.DefaultRequestHeaders.Remove("Idempotency-Key");
            owner.DefaultRequestHeaders.Add("Idempotency-Key", "publish-stale");
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/v1/missions/{missionId}/revisions", publish, RingfourJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/v1/missions/{missionId}/revisions", publish with { expected_version = 1, manifest = manifest with { source_digest = new string('0', 64) } }, RingfourJson.Options)).StatusCode);
            var page = JsonDocument.Parse(await owner.GetStringAsync($"/v1/missions/{missionId}/revisions")).RootElement;
            Assert.Single(page.GetProperty("items").EnumerateArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
