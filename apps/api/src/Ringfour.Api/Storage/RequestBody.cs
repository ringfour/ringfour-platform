using Ringfour.Api.Errors;
namespace Ringfour.Api.Storage;

internal static class RequestBody
{
    internal static async Task<byte[]> ReadLimited(Stream stream, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (output.Length + read > limit) throw new ApiException(413, "size_limit", "Request body exceeds its byte limit.");
            await output.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
}
