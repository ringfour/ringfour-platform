using Amazon.S3;
using Amazon.S3.Model;

namespace Ringfour.Api.Storage;

/// <summary>Private, bounded storage for server-generated keys.</summary>
public interface IObjectStore
{
    Task CheckReadyAsync(CancellationToken cancellationToken);
    Task PutAsync(string key, byte[] data, CancellationToken cancellationToken);
    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public static class ObjectStoreKey
{
    public const int MaxObjectBytes = 1048576;

    public static void Validate(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 240 || key.StartsWith('/') ||
            key.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".." ||
                part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
        {
            throw new ArgumentException("Invalid object key.", nameof(key));
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + count > MaxObjectBytes)
                throw new InvalidDataException("Stored object exceeds the byte limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
        return buffer.ToArray();
    }

    public static void ValidateSize(byte[] data)
    {
        if (data.Length > MaxObjectBytes)
            throw new InvalidDataException("Object exceeds the byte limit.");
    }
}

public sealed class FileObjectStore(string root) : IObjectStore
{
    private readonly string root = Path.GetFullPath(root);

    private string Resolve(string key)
    {
        ObjectStoreKey.Validate(key);
        return Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".readiness-" + Guid.NewGuid().ToString("N"));
        try { await File.WriteAllBytesAsync(path, [], cancellationToken); }
        finally { File.Delete(path); }
    }

    public async Task PutAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        ObjectStoreKey.ValidateSize(data);
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Resolve(key));
        return await ObjectStoreKey.ReadBoundedAsync(stream, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(Resolve(key)); }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }
}

public sealed class S3ObjectStore(IAmazonS3 client, string bucket) : IObjectStore, IDisposable
{
    public async Task CheckReadyAsync(CancellationToken cancellationToken) =>
        await client.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, cancellationToken);

    public async Task PutAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        ObjectStoreKey.Validate(key);
        ObjectStoreKey.ValidateSize(data);
        using var stream = new MemoryStream(data, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        }, cancellationToken);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        ObjectStoreKey.Validate(key);
        using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
        if (response.ContentLength > ObjectStoreKey.MaxObjectBytes)
            throw new InvalidDataException("Stored object exceeds the byte limit.");
        return await ObjectStoreKey.ReadBoundedAsync(response.ResponseStream, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ObjectStoreKey.Validate(key);
        await client.DeleteObjectAsync(bucket, key, cancellationToken);
    }

    public void Dispose() => client.Dispose();
}
