using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ringfour.Api.Tests;

// Component fixture: real HTTP transport, controlled registry responses.
// Real Distribution registry acceptance is a separate process harness.
internal sealed class RegistryFixture : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly byte[] config;
    private readonly byte[] manifest;
    private readonly ConcurrentDictionary<string, DateTimeOffset> tokens = new();
    public string Origin { get; }
    public string Host => new Uri(Origin).Authority;
    public string Image => Host + "/apps/check@" + Hash(manifest);
    public bool Private { get; set; }
    public bool Corrupt { get; set; }
    public bool Redirect { get; set; }
    public int TokenSeconds { get; set; } = 120;
    public int Requests;
    public Func<Task>? BeforeConfig { get; set; }
    public ConcurrentQueue<string> Scopes { get; } = new();
    public ConcurrentQueue<string> IssuedTokens { get; } = new();
    public string MediaType { get; set; } = "application/vnd.oci.image.manifest.v1+json";
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public RegistryFixture(string architecture = "amd64")
    {
        config = Encoding.UTF8.GetBytes(new JsonObject { ["os"] = "linux", ["architecture"] = architecture }.ToJsonString());
        manifest = Encoding.UTF8.GetBytes(new JsonObject { ["schemaVersion"] = 2, ["mediaType"] = MediaType,
            ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.image.config.v1+json", ["digest"] = Hash(config), ["size"] = config.Length },
            ["layers"] = new JsonArray() }.ToJsonString());
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build(); app.Run(Handle); app.StartAsync().GetAwaiter().GetResult();
        Origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public Dictionary<string, string?> Configuration() => new()
    {
        ["Apps:Registries:0:Host"] = Host, ["Apps:Registries:0:RepositoryPrefix"] = "apps",
        ["Apps:Registries:0:AllowHttp"] = "true", ["Apps:Registries:0:AnonymousPull"] = Private ? "false" : "true",
        ["Apps:Registries:0:TokenEndpoint"] = Origin + "/token", ["Apps:Registries:0:Service"] = "ap05-fixture",
        ["Apps:Registries:0:Username"] = "fixture-reader", ["Apps:Registries:0:Password"] = "fixture-password",
    };

    private async Task Handle(HttpContext context)
    {
        Interlocked.Increment(ref Requests);
        if (context.Request.Path == "/token")
        {
            var scope = context.Request.Query["scope"].ToString(); Scopes.Enqueue(scope);
            if (context.Request.Headers.Authorization != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-reader:fixture-password")) || scope != "repository:apps/check:pull")
            { context.Response.StatusCode = 403; return; }
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            tokens[token] = DateTimeOffset.UtcNow.AddSeconds(TokenSeconds); IssuedTokens.Enqueue(token);
            await context.Response.WriteAsJsonAsync(new { token, expires_in = TokenSeconds }); return;
        }
        if (Redirect) { context.Response.Redirect(Origin + "/unexpected"); return; }
        if (Private)
        {
            var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            if (!tokens.TryGetValue(token, out var expires) || expires <= DateTimeOffset.UtcNow)
            { context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = $"Bearer realm=\"{Origin}/token\",service=\"ap05-fixture\",scope=\"repository:apps/check:pull,push\""; return; }
        }
        if (context.Request.Path == "/v2/apps/check/manifests/" + Hash(manifest))
        { context.Response.ContentType = MediaType; await context.Response.Body.WriteAsync(Corrupt ? "{}"u8.ToArray() : manifest); return; }
        if (context.Request.Path == "/v2/apps/check/blobs/" + Hash(config))
        { if (BeforeConfig is not null) await BeforeConfig(); await context.Response.Body.WriteAsync(config); return; }
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
    }

    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
