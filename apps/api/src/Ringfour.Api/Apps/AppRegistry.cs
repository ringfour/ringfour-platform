using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Apps;

public sealed record RegistryAuthorization(string Registry, string Repository, string? Token, DateTimeOffset ExpiresAt)
{
    public long SizeBytes { get; init; }
}

public sealed class AppRegistry(HttpClient client, IConfiguration configuration, IHostEnvironment environment, ManagedAppRegistry managed)
{
    private const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    private const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private sealed record Policy(string Host, string Prefix, string Scheme, string ApiEndpoint, bool Anonymous, string? Endpoint, string? Service, string? Username, string? Password);

    private static ApiException Unavailable(bool retryable = true) => new(503, "revision_unavailable", "The configured registry could not verify this image.") { Retryable = retryable };
    private static bool DigestValid(string? value) => value is not null && Regex.IsMatch(value, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static string Hash(byte[] value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));

    private Policy Resolve(string image, Guid publisher, out string repository, out string digest)
    {
        var slash = image.IndexOf('/'); var at = image.IndexOf('@');
        if (slash <= 0 || at <= slash || !DigestValid(image[(at + 1)..])) throw ApiException.InvalidInput("A digest-pinned registry image is required.");
        var host = image[..slash]; repository = image[(slash + 1)..at]; digest = image[(at + 1)..];
        if (host.Length > 255 || repository.Length > 255) throw Unavailable(false);
        var repo = repository;
        var entry = configuration.GetSection("Apps:Registries").GetChildren().FirstOrDefault(x =>
            x["Host"] == host && !string.IsNullOrEmpty(x["RepositoryPrefix"]) &&
            (repo == x["RepositoryPrefix"]!.TrimEnd('/') || repo.StartsWith(x["RepositoryPrefix"]!.TrimEnd('/') + "/", StringComparison.Ordinal)) &&
            (string.IsNullOrEmpty(x["PublisherOrganizationId"]) ? x.GetValue("AnonymousPull", false) :
                Guid.TryParse(x["PublisherOrganizationId"], out var owner) && owner == publisher));
        if (entry is null) throw Unavailable(false);
        var scheme = entry.GetValue("AllowHttp", false) && environment.IsDevelopment() ? "http" : "https";
        var apiEndpoint = $"{scheme}://{host}";
        if (!string.IsNullOrEmpty(entry["ApiEndpoint"]))
        {
            // Local Docker clients use loopback; the containerized API uses Compose DNS.
            if (!environment.IsDevelopment() || !entry.GetValue("AllowHttp", false) ||
                !Uri.TryCreate("http://" + host, UriKind.Absolute, out var authority) || !authority.IsLoopback ||
                !Uri.TryCreate(entry["ApiEndpoint"], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
                endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.AbsolutePath != "/")
                throw Unavailable(false);
            apiEndpoint = endpoint.GetLeftPart(UriPartial.Authority);
        }
        return new Policy(host, entry["RepositoryPrefix"]!, scheme, apiEndpoint, entry.GetValue("AnonymousPull", false),
            entry["TokenEndpoint"], entry["Service"], entry["Username"], entry["Password"]);
    }

    public async Task<RegistryAuthorization> Verify(JsonObject target, Guid publisher, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        try
        {
            var image = target["image"]!.GetValue<string>();
            var ownRegistry = managed.Owns(image);
            string repository;
            string digest;
            Policy policy;
            if (ownRegistry)
            {
                var separator = image.IndexOf('@');
                if (separator < 0 || !DigestValid(image[(separator + 1)..])) throw Unavailable(false);
                repository = image[(managed.Host!.Length + 1)..separator];
                digest = image[(separator + 1)..];
                var app = repository.Split('/').Last();
                if (!Guid.TryParseExact(app, "D", out var appId) || repository != ManagedAppRegistry.Repository(publisher, appId))
                    throw Unavailable(false);
                policy = new Policy(managed.Host, "apps", "https", managed.Endpoint, false, null, null, null, null);
            }
            else policy = Resolve(image, publisher, out repository, out digest);
            var path = $"{policy.ApiEndpoint}/v2/{repository}/";
            RegistryAuthorization? authorization = ownRegistry
                ? new(policy.Host, repository, managed.Issue("platform-verifier", TimeSpan.FromMinutes(1), new RegistryScope(repository, ["pull"])), DateTimeOffset.UtcNow.AddMinutes(1))
                : null;
            using var first = await Send(path + "manifests/" + digest, authorization?.Token, ct);
            HttpResponseMessage? authenticated = null;
            try
            {
                if (!ownRegistry && first.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var challenge = first.Headers.WwwAuthenticate.SingleOrDefault(x => x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
                    authorization = await Exchange(policy, repository, challenge?.Parameter, ct);
                    authenticated = await Send(path + "manifests/" + digest, authorization.Token, ct);
                }
                else if (!ownRegistry && !policy.Anonymous) throw Unavailable(false);
                var response = authenticated ?? first;
                if (!response.IsSuccessStatusCode) throw Unavailable();
                var type = response.Content.Headers.ContentType?.MediaType;
                if (type is not (OciManifest or DockerManifest)) throw Unavailable(false);
                var bytes = await Read(response, 1048576, ct);
                if (Hash(bytes) != digest || (response.Headers.TryGetValues("Docker-Content-Digest", out var hashes) && hashes.Single() != digest))
                    throw Unavailable(false);
                var manifest = JsonNode.Parse(bytes)!.AsObject();
                if (manifest["schemaVersion"]?.GetValue<int>() != 2 || manifest["mediaType"]?.GetValue<string>() != type || manifest["layers"] is not JsonArray)
                    throw Unavailable(false);
                var config = manifest["config"]!.AsObject();
                var configType = config["mediaType"]?.GetValue<string>();
                if (configType != (type == OciManifest ? "application/vnd.oci.image.config.v1+json" : "application/vnd.docker.container.image.v1+json"))
                    throw Unavailable(false);
                var configHash = config["digest"]!.GetValue<string>();
                if (!DigestValid(configHash)) throw Unavailable(false);
                using var blob = await Send(path + "blobs/" + configHash, authorization?.Token, ct);
                if (!blob.IsSuccessStatusCode) throw Unavailable();
                var configBytes = await Read(blob, 65536, ct);
                if (Hash(configBytes) != configHash || config["size"]!.GetValue<long>() != configBytes.Length) throw Unavailable(false);
                var platform = JsonNode.Parse(configBytes)!;
                if (platform["os"]?.GetValue<string>() != target["os"]!.GetValue<string>() ||
                    platform["architecture"]?.GetValue<string>() != target["architecture"]!.GetValue<string>()) throw Unavailable(false);
                if (ownRegistry)
                {
                    long size = bytes.Length + configBytes.Length;
                    var seen = new HashSet<string> { configHash };
                    var layers = manifest["layers"]!.AsArray();
                    if (layers.Count > 256) throw Unavailable(false);
                    // Docker Registry validates blob membership when storing a manifest. Check it
                    // again here and derive image size from independently verified descriptors.
                    foreach (var layer in layers)
                    {
                        var hash = layer?["digest"]?.GetValue<string>();
                        var length = layer?["size"]?.GetValue<long>() ?? -1;
                        if (!DigestValid(hash) || length < 0) throw Unavailable(false);
                        using var content = await Send(path + "blobs/" + hash, authorization!.Token, ct, HttpMethod.Head);
                        if (!content.IsSuccessStatusCode || content.Content.Headers.ContentLength != length) throw Unavailable(false);
                        if (seen.Add(hash!)) size = checked(size + length);
                        if (size > 9007199254740991) throw Unavailable(false);
                    }
                    return authorization! with { SizeBytes = size };
                }
                if (policy.Anonymous)
                    return new RegistryAuthorization(policy.Host, repository, null, DateTimeOffset.UtcNow.AddMinutes(5));
                return authorization ?? throw Unavailable(false);
            }
            finally { authenticated?.Dispose(); }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException or NullReferenceException or OverflowException ||
                                      (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        { throw Unavailable(); }
    }

    private async Task<RegistryAuthorization> Exchange(Policy policy, string repository, string? challenge, CancellationToken ct)
    {
        var fields = Regex.Matches(challenge ?? "", "([a-z_]+)=\"([^\"]*)\"").ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);
        if (policy.Endpoint is null || policy.Service is null || fields.GetValueOrDefault("realm") != policy.Endpoint || fields.GetValueOrDefault("service") != policy.Service ||
            !Uri.TryCreate(policy.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
            (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && policy.Scheme == "http"))) throw Unavailable(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, policy.Endpoint + "?service=" + Uri.EscapeDataString(policy.Service) +
            "&scope=" + Uri.EscapeDataString($"repository:{repository}:pull") + "&expires_in=300");
        if (!policy.Anonymous)
        {
            if (string.IsNullOrEmpty(policy.Username) || string.IsNullOrEmpty(policy.Password)) throw Unavailable(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.Username + ":" + policy.Password)));
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw Unavailable();
        var value = JsonNode.Parse(await Read(response, 16384, ct))!;
        var token = (value["token"] ?? value["access_token"])?.GetValue<string>();
        var seconds = value["expires_in"]?.GetValue<int>() ?? 60;
        var issued = value["issued_at"] is null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(value["issued_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var expires = issued.AddSeconds(seconds);
        if (string.IsNullOrEmpty(token) || token.Length > 8192 || token.Any(char.IsWhiteSpace) || !Regex.IsMatch(token, "^[A-Za-z0-9._~+/-]+=*$") || seconds is < 1 or > 300 ||
            issued > DateTimeOffset.UtcNow.AddSeconds(30) || expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddMinutes(5)) throw Unavailable(false);
        return new RegistryAuthorization(policy.Host, repository, token, expires);
    }

    private async Task<HttpResponseMessage> Send(string url, string? token, CancellationToken ct, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(OciManifest + ", " + DockerManifest);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<byte[]> Read(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw Unavailable(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + count > limit) throw Unavailable(false); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
}
