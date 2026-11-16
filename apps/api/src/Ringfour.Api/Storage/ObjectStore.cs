using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Text.RegularExpressions;

namespace Ringfour.Api.Storage;

/// <summary>Private, bounded storage for server-generated keys.</summary>
public interface IObjectStore
{
    Task CheckReadyAsync(CancellationToken cancellationToken);
    Task PutAsync(string key, byte[] data, CancellationToken cancellationToken);
    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken);
    Task PutFileAsync(string key, string sourcePath, CancellationToken cancellationToken);
    Task CopyToAsync(string key, Stream destination, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

internal interface IInterruptedTransferCleanup
{
    Task CleanupInterruptedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public static class ObjectStoreKey
{
    public const int MaxObjectBytes = 1048576;

    public static bool IsTransferKey(string key) => Regex.IsMatch(key,
        "^(?:(?:incoming|revisions)/[0-9a-f]{32}|artifacts/[0-9a-f]{32}/[0-9a-f]{64})$",
        RegexOptions.CultureInvariant);

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

public sealed class FileObjectStore(string root) : IObjectStore, IInterruptedTransferCleanup
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

    public async Task PutFileAsync(string key, string sourcePath, CancellationToken cancellationToken)
    {
        var destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (TemporaryTransfer.AvailableSpace(Path.GetDirectoryName(destination)) < source.Length)
                    throw new InsufficientTransferStorageException();
                var buffer = new byte[65536];
                long written = 0, nextCheck = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (written >= nextCheck)
                    {
                        if (TemporaryTransfer.AvailableSpace(Path.GetDirectoryName(destination)) < count)
                            throw new InsufficientTransferStorageException();
                        nextCheck = written + 16 * 1024 * 1024;
                    }
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    written += count;
                }
                await target.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public async Task CopyToAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 65536, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(Resolve(key)); }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    public Task CleanupInterruptedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return Task.CompletedTask;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*.tmp", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var parts = name.Split('.');
            if (parts.Length < 3 || parts[^1] != "tmp" || !Guid.TryParseExact(parts[^2], "N", out _)) continue;
            var key = Path.GetRelativePath(root, file)[..^(parts[^2].Length + 5)].Replace(Path.DirectorySeparatorChar, '/');
            if (!ObjectStoreKey.IsTransferKey(key)) continue;
            if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) File.Delete(file);
        }
        return Task.CompletedTask;
    }
}

public sealed class S3ObjectStore(IAmazonS3 client, string bucket) : IObjectStore, IInterruptedTransferCleanup, IDisposable
{
    public async Task CheckReadyAsync(CancellationToken cancellationToken) =>
        await client.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, cancellationToken);

    public async Task CleanupInterruptedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        foreach (var prefix in new[] { "incoming/", "revisions/", "artifacts/" })
        {
            string? keyMarker = null, uploadMarker = null;
            do
            {
                var page = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
                {
                    BucketName = bucket, Prefix = prefix, KeyMarker = keyMarker, UploadIdMarker = uploadMarker,
                }, cancellationToken);
                foreach (var upload in page.MultipartUploads ?? [])
                {
                    if (ObjectStoreKey.IsTransferKey(upload.Key) && upload.Initiated < cutoff.UtcDateTime)
                        await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                        {
                            BucketName = bucket, Key = upload.Key, UploadId = upload.UploadId,
                        }, cancellationToken);
                }
                keyMarker = page.IsTruncated == true ? page.NextKeyMarker : null;
                uploadMarker = page.IsTruncated == true ? page.NextUploadIdMarker : null;
            } while (keyMarker is not null);
        }
    }

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

    public async Task PutFileAsync(string key, string sourcePath, CancellationToken cancellationToken)
    {
        ObjectStoreKey.Validate(key);
        var length = new FileInfo(sourcePath).Length;
        const long minimumPartSize = 8L * 1024 * 1024;
        if (length <= minimumPartSize)
        {
            await using var stream = File.OpenRead(sourcePath);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, InputStream = stream,
                AutoCloseStream = false, UseChunkEncoding = false,
            }, cancellationToken);
            return;
        }
        // At most 10,000 parts; avoid SigV4 streaming chunk signatures that
        // S3-compatible stores such as Garage do not necessarily support.
        var partSize = Math.Max(minimumPartSize, ((length - 1) / 10000) + 1);
        var initiated = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = key,
        }, cancellationToken);
        try
        {
            var completed = new List<PartETag>();
            for (long offset = 0; offset < length; offset += partSize)
            {
                var response = await client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = bucket, Key = key, UploadId = initiated.UploadId,
                    FilePath = sourcePath, FilePosition = offset, PartNumber = completed.Count + 1,
                    PartSize = Math.Min(partSize, length - offset), UseChunkEncoding = false,
                }, cancellationToken);
                completed.Add(new PartETag(completed.Count + 1, response.ETag));
            }
            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = key, UploadId = initiated.UploadId, PartETags = completed,
            }, cancellationToken);
        }
        catch
        {
            try { await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = initiated.UploadId }, CancellationToken.None); }
            catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or IOException) { /* Original failure is authoritative. */ }
            throw;
        }
    }

    public async Task CopyToAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        ObjectStoreKey.Validate(key);
        using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
        await response.ResponseStream.CopyToAsync(destination, 65536, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ObjectStoreKey.Validate(key);
        await client.DeleteObjectAsync(bucket, key, cancellationToken);
    }

    public void Dispose() => client.Dispose();
}
