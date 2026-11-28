using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Ringfour.Api.Apps;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Tests;

public sealed class ManagedAppRegistryTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("ringfour-registry-signing-").FullName;
    private readonly RSA key = RSA.Create(2048);
    private const string Service = "ringfour-app-registry";
    private const string Host = "registry.ringfour.eu";
    private const string ManifestType = "application/vnd.oci.image.manifest.v1+json";

    internal static Dictionary<string, string?> Settings(string directory, RSA key)
    {
        var certPath = Path.Combine(directory, "cert.pem");
        var keyPath = Path.Combine(directory, "key.pem");
        using var cert = new CertificateRequest("CN=Ringfour registry test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return new()
        {
            ["Apps:ManagedRegistry:Enabled"] = "true",
            ["Apps:ManagedRegistry:CertificatePath"] = certPath,
            ["Apps:ManagedRegistry:KeyPath"] = keyPath,
        };
    }

    public void Dispose()
    {
        key.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private static string Digest(byte[] value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));
    private static byte[] Bytes(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    private sealed class RegistryEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "registry-test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RegistryDouble(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manifest_and_layer_metadata_are_verified_with_a_repository_pull_token(bool layerExists)
    {
        var organization = Guid.NewGuid();
        var repository = $"apps/{organization:D}/{Guid.NewGuid():D}";
        var layerDigest = Digest("layer"u8.ToArray());
        var config = """{"os":"linux","architecture":"amd64"}"""u8.ToArray();
        var manifest = Bytes(new JsonObject
        {
            ["schemaVersion"] = 2, ["mediaType"] = ManifestType,
            ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.image.config.v1+json", ["size"] = config.Length, ["digest"] = Digest(config) },
            ["layers"] = new JsonArray(new JsonObject { ["digest"] = layerDigest, ["size"] = 5 })
        });
        var prefix = "/v2/" + repository;
        var requests = 0;
        using var client = new HttpClient(new RegistryDouble(request =>
        {
            requests++;
            Assert.Equal("app-registry", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(request.Headers.Authorization.Parameter, new TokenValidationParameters
            {
                ValidIssuer = "ringfour-platform", ValidAudience = Service, IssuerSigningKey = new RsaSecurityKey(key),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidateIssuerSigningKey = true
            }, out var validated);
            var access = JsonNode.Parse(Base64UrlEncoder.DecodeBytes(((JwtSecurityToken)validated).EncodedPayload))!["access"]![0]!;
            Assert.Equal(repository, access["name"]!.GetValue<string>());
            Assert.Equal(new[] { "pull" }, access["actions"]!.Deserialize<string[]>());
            var path = request.RequestUri.AbsolutePath;
            if (path == prefix + "/blobs/" + layerDigest)
            {
                Assert.Equal(HttpMethod.Head, request.Method);
                return new HttpResponseMessage(layerExists ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                    { Content = new ByteArrayContent("layer"u8.ToArray()) };
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            var isManifest = path == prefix + "/manifests/" + Digest(manifest);
            Assert.True(isManifest || path == prefix + "/blobs/" + Digest(config));
            var content = new ByteArrayContent(isManifest ? manifest : config);
            if (isManifest) content.Headers.ContentType = new MediaTypeHeaderValue(ManifestType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(directory, key)).Build();
        using var managed = new ManagedAppRegistry(configuration);
        var registry = new AppRegistry(client, configuration, new RegistryEnvironment(), managed);
        var target = new JsonObject { ["image"] = $"{Host}/{repository}@{Digest(manifest)}", ["os"] = "linux", ["architecture"] = "amd64" };
        if (layerExists)
            Assert.Equal(manifest.Length + config.Length + 5, (await registry.Verify(target, organization, default)).SizeBytes);
        else
            await Assert.ThrowsAsync<ApiException>(() => registry.Verify(target, organization, default));
        Assert.Equal(3, requests);
        await Assert.ThrowsAsync<ApiException>(() => registry.Verify(target, Guid.NewGuid(), default));
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_endpoint_rejects_tampered_and_expired_upload_credentials(bool expired)
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Database=unused", migrateOnStartup: false, extraConfiguration: Settings(directory, key));
        using var client = factory.CreateClient();
        using var wrongKey = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var signed = new JwtSecurityToken("ringfour-platform", Service + ":upload",
            notBefore: now.AddMinutes(-5), expires: expired ? now.AddMinutes(-1) : now.AddMinutes(1),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(expired ? key : wrongKey), SecurityAlgorithms.RsaSha256));
        var bearer = new JwtSecurityTokenHandler().WriteToken(signed);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("ringfour:" + bearer)));
        var response = await client.GetAsync($"/registry/token?service={Service}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ManagedAppRegistryAuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public Task Upload_reservation_and_token_exchange_isolate_workspaces() =>
        VerifyWorkspaceIsolation(postgres.ConnectionString);

    internal static async Task VerifyWorkspaceIsolation(string connectionString)
    {
        var directory = Directory.CreateTempSubdirectory("ringfour-registry-auth-");
        using var key = RSA.Create(2048);
        try
        {
            await using var factory = new ApiFactory(connectionString,
                extraConfiguration: ManagedAppRegistryTests.Settings(directory.FullName, key));
            using var owner = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
            using var other = factory.CreateAuthenticatedClient("auth0|" + Guid.NewGuid());
            using var exchange = factory.CreateClient();
            var first = await OrganizationTests.CreateOrganization(owner, "Registry Publisher");
            var second = await OrganizationTests.CreateOrganization(other, "Other Registry Publisher");
            static string Upload(Guid org) => $"/v1/organizations/{org:D}/apps/greeting/registry-upload";
            static string Token(string scope) => "/registry/token?service=ringfour-app-registry&scope=" + Uri.EscapeDataString(scope);
            static string Repository(JsonObject grant) => grant["repository"]!.GetValue<string>().Split('/', 2)[1];

            using var firstResponse = await owner.PostAsync(Upload(first), null);
            firstResponse.EnsureSuccessStatusCode();
            var grant = (await firstResponse.Content.ReadFromJsonAsync<JsonObject>())!;
            using var retried = await owner.PostAsync(Upload(first), null);
            Assert.Equal(Repository(grant), Repository((await retried.Content.ReadFromJsonAsync<JsonObject>())!));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(Upload(first), null)).StatusCode);

            using var secondResponse = await other.PostAsync(Upload(second), null);
            secondResponse.EnsureSuccessStatusCode();
            var otherRepository = Repository((await secondResponse.Content.ReadFromJsonAsync<JsonObject>())!);
            var repository = Repository(grant);
            Assert.StartsWith($"apps/{first:D}/", repository);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Token("repository:" + repository + ":pull"))).StatusCode);

            exchange.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes("ringfour:" + grant["credential"]!.GetValue<string>())));
            Assert.Equal(HttpStatusCode.Forbidden,
                (await exchange.GetAsync(Token("repository:" + otherRepository + ":pull,push"))).StatusCode);
            using var tokenResponse = await exchange.GetAsync(Token("repository:" + repository + ":pull,push,delete"));
            tokenResponse.EnsureSuccessStatusCode();
            var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var access = JsonNode.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload))!["access"]!.AsArray();
            Assert.Single(access);
            Assert.Equal(repository, access[0]!["name"]!.GetValue<string>());
            Assert.Equal(new[] { "pull", "push" }, access[0]!["actions"]!.Deserialize<string[]>());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
