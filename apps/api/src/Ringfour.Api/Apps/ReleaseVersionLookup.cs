using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ringfour.Api.Apps;

/// <summary>Cached, read-only versions from completed GitHub Releases.</summary>
public sealed class ReleaseVersionLookup(HttpClient client)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private DateTimeOffset _nextRefresh;
    private (string? Agent, string? Daemon) _latest;

    public async Task<(string? Agent, string? Daemon)> Latest(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextRefresh) return _latest;
        await _refresh.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _nextRefresh) return _latest;
            var agent = ReadAgent(ct);
            var daemon = ReadDaemon(ct);
            _latest = (await agent, await daemon);
            _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(
                _latest.Agent is null || _latest.Daemon is null ? 5 : 15);
            return _latest;
        }
        finally { _refresh.Release(); }
    }

    private async Task<string?> ReadAgent(CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("repos/ringfour/ringfour-agent/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var tag = document.RootElement.GetProperty("tag_name").GetString();
            return tag is not null && Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)
                ? tag[1..] : null;
        }
        catch (Exception error) when ((error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException) && !ct.IsCancellationRequested)
        { return null; }
    }

    private async Task<string?> ReadDaemon(CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("repos/ringfour/ringfour-platform/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var assets = root.GetProperty("assets").EnumerateArray()
                .ToDictionary(asset => asset.GetProperty("name").GetString()!, asset => asset.GetProperty("id").GetInt64());
            if (tag is null || !Regex.IsMatch(tag, @"^main-[0-9]+-[0-9]+$", RegexOptions.CultureInvariant) ||
                !assets.ContainsKey("daemon-version.txt") || !assets.ContainsKey("SHA256SUMS"))
                return null;
            var sums = Encoding.UTF8.GetString(await Asset(assets["SHA256SUMS"], ct));
            var matches = Regex.Matches(sums, @"^([a-f0-9]{64})  daemon-version\.txt\r?$", RegexOptions.Multiline);
            if (matches.Count != 1) return null;
            var bytes = await Asset(assets["daemon-version.txt"], ct);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != matches[0].Groups[1].Value) return null;
            var value = Encoding.UTF8.GetString(bytes).Trim();
            return Regex.IsMatch(value, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)
                ? value : null;
        }
        catch (Exception error) when ((error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException) && !ct.IsCancellationRequested)
        { return null; }
    }

    private async Task<byte[]> Asset(long id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"repos/ringfour/ringfour-platform/releases/assets/{id}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
