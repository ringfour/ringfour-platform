using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Ringfour.Api.Apps;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Tests;

public sealed class AppRegistryRoutingTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "registry-test";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static (AppRegistry Registry, JsonObject Target) Consumer(RegistryFixture backend, HttpClient client,
        string environment = "Development", string host = "127.0.0.1:5000", bool http = true, string? endpoint = null)
    {
        var settings = backend.Configuration(); settings["Apps:Registries:0:Host"] = host;
        settings["Apps:Registries:0:AllowHttp"] = http.ToString();
        settings["Apps:Registries:0:ApiEndpoint"] = endpoint ?? backend.Origin;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var image = host + backend.Image[backend.Image.IndexOf('/')..];
        return (new AppRegistry(client, configuration, new Environment(environment), new ManagedAppRegistry(configuration)),
            new JsonObject { ["image"] = image, ["os"] = "linux", ["architecture"] = "amd64" });
    }

    [Fact]
    public async Task Development_route_verifies_internal_bytes_and_retains_external_image_authority()
    {
        await using var backend = new RegistryFixture(); using var client = new HttpClient();
        var (registry,target) = Consumer(backend,client);
        var result = await registry.Verify(target,Guid.NewGuid(),CancellationToken.None);
        Assert.Equal("127.0.0.1:5000",result.Registry); Assert.Equal("apps/check",result.Repository);
        Assert.Null(result.Token); Assert.Equal(2,backend.Requests);
        Assert.StartsWith("127.0.0.1:5000/apps/check@sha256:",target["image"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Production", "127.0.0.1:5000", true, "http://backend:5000")]
    [InlineData("Development", "registry.example.com", true, "http://backend:5000")]
    [InlineData("Development", "127.0.0.1:5000", false, "http://backend:5000")]
    [InlineData("Development", "127.0.0.1:5000", true, "https://backend:5000")]
    [InlineData("Development", "127.0.0.1:5000", true, "http://user:fixture@backend:5000")]
    [InlineData("Development", "127.0.0.1:5000", true, "http://backend:5000/path")]
    [InlineData("Development", "127.0.0.1:5000", true, "http://backend:5000/?query=1")]
    [InlineData("Development", "127.0.0.1:5000", true, "http://backend:5000/#fragment")]
    public async Task Internal_route_is_rejected_outside_local_development_before_network_access(string environment,string host,bool http,string endpoint)
    {
        await using var backend = new RegistryFixture(); using var client = new HttpClient();
        var (registry,target) = Consumer(backend,client,environment,host,http,endpoint);
        var error = await Assert.ThrowsAsync<ApiException>(() => registry.Verify(target,Guid.NewGuid(),CancellationToken.None));
        Assert.Equal("revision_unavailable",error.Code); Assert.False(error.Retryable); Assert.Equal(0,backend.Requests);
    }
}
