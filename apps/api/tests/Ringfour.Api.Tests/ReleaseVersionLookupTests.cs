using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ringfour.Api.Apps;

namespace Ringfour.Api.Tests;

public sealed class ReleaseVersionLookupTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Reads_both_published_versions_and_caches_them()
    {
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var daemonVersion = "1.0.4\n";
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(daemonVersion)));
            var content = path switch
            {
                "/repos/ringfour/ringfour-agent/releases/latest" => "{\"tag_name\":\"v1.2.3\"}",
                "/repos/ringfour/ringfour-platform/releases/latest" => "{\"tag_name\":\"main-123-1\",\"assets\":[{\"name\":\"daemon-version.txt\",\"id\":1},{\"name\":\"SHA256SUMS\",\"id\":2}]}",
                "/repos/ringfour/ringfour-platform/releases/assets/2" => $"{digest}  daemon-version.txt\n",
                "/repos/ringfour/ringfour-platform/releases/assets/1" => daemonVersion,
                _ => throw new InvalidOperationException(path),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var lookup = new ReleaseVersionLookup(client);
        Assert.Equal(("1.2.3", "1.0.4"), await lookup.Latest(CancellationToken.None));
        Assert.Equal(("1.2.3", "1.0.4"), await lookup.Latest(CancellationToken.None));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task Missing_or_malformed_releases_have_unknown_versions()
    {
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = path.Contains("ringfour-agent") ? "{\"tag_name\":\"preview\"}" :
                "{\"tag_name\":\"main-123-1\",\"assets\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var lookup = new ReleaseVersionLookup(client);
        Assert.Equal((null, null), await lookup.Latest(CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Rejects_a_Daemon_version_with_the_wrong_release_checksum()
    {
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = path switch
            {
                "/repos/ringfour/ringfour-agent/releases/latest" => "{\"tag_name\":\"v1.0.0\"}",
                "/repos/ringfour/ringfour-platform/releases/latest" => "{\"tag_name\":\"main-123-1\",\"assets\":[{\"name\":\"daemon-version.txt\",\"id\":1},{\"name\":\"SHA256SUMS\",\"id\":2}]}",
                "/repos/ringfour/ringfour-platform/releases/assets/2" => $"{new string('0', 64)}  daemon-version.txt\n",
                "/repos/ringfour/ringfour-platform/releases/assets/1" => "1.0.0\n",
                _ => throw new InvalidOperationException(path),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var lookup = new ReleaseVersionLookup(client);
        Assert.Equal(("1.0.0", null), await lookup.Latest(CancellationToken.None));
    }
}
