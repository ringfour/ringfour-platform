using System.Security.Cryptography;
using Amazon.Runtime;
using Ringfour.Api.Errors;

namespace Ringfour.Api.Storage;

internal sealed class InsufficientTransferStorageException() : IOException("Insufficient free transfer storage.");

/// <summary>A private, bounded-memory staging file deleted on completion or failure.</summary>
internal sealed class TemporaryTransfer : IAsyncDisposable
{
    private TemporaryTransfer(string path, long length, string sha256)
    {
        Path = path;
        Length = length;
        Sha256 = sha256;
    }

    public string Path { get; }
    public long Length { get; }
    public string Sha256 { get; }
    private bool transferred;

    public static async Task<IResult> VerifiedResponseAsync(HttpContext context, IObjectStore store, string key,
        long expectedSize, string expectedSha256, string contentType, string? downloadName, CancellationToken ct)
    {
        TemporaryTransfer result;
        try { result = await FromStoreAsync(store, key, expectedSize, ct); }
        catch (InsufficientTransferStorageException)
        { throw new ApiException(503, "insufficient_storage", "Not enough temporary storage for this transfer."); }
        catch (Exception exception) when (exception is IOException or AmazonServiceException or AmazonClientException or HttpRequestException)
        { throw new ApiException(503, "storage_unavailable", "The artifact store is unavailable."); }
        await using var temporary = result;
        if (temporary.Length != expectedSize || temporary.Sha256 != expectedSha256)
            throw new ApiException(503, "storage_unavailable", "The artifact store returned invalid bytes.");
        var file = new FileStream(temporary.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        context.Response.RegisterForDispose(file);
        temporary.transferred = true;
        return Results.File(file, contentType, downloadName);
    }

    // DriveInfo roots include separate container mounts; use the deepest match.
    public static long AvailableSpace(string? path = null)
    {
        var directory = System.IO.Path.GetFullPath(path ?? System.IO.Path.GetTempPath());
        var drive = DriveInfo.GetDrives().Where(item => directory.StartsWith(item.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(item => item.RootDirectory.FullName.Length).FirstOrDefault();
        return Math.Max(0, (drive?.AvailableFreeSpace ?? 0) - 268435456);
    }

    public static void CleanupInterrupted(DateTimeOffset cutoff)
    {
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.GetTempPath(), "ringfour-transfer-*").Take(1000))
        {
            var name = System.IO.Path.GetFileName(file);
            if (Guid.TryParseExact(name["ringfour-transfer-".Length..], "N", out _) && File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                File.Delete(file);
        }
    }

    public static async Task<TemporaryTransfer> FromRequestAsync(Stream source, long limit, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ringfour-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.SequentialScan };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var destination = new FileStream(path, options);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            long nextSpaceCheck = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                if (read > limit - total) throw new InvalidDataException("Transfer exceeds its allowed size.");
                if (total >= nextSpaceCheck)
                {
                    if (AvailableSpace() < read) throw new InsufficientTransferStorageException();
                    nextSpaceCheck = total + 16 * 1024 * 1024;
                }
                total += read;
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await destination.FlushAsync(ct);
            return new TemporaryTransfer(path, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public static async Task<TemporaryTransfer> FromStoreAsync(IObjectStore store, string key, long limit, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ringfour-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (AvailableSpace() < limit) throw new InsufficientTransferStorageException();
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
                Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.SequentialScan };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var file = new FileStream(path, options);
            await using var bounded = new BoundedWriteStream(file, limit);
            await store.CopyToAsync(key, bounded, ct);
            await file.FlushAsync(ct);
            file.Position = 0;
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            return new TemporaryTransfer(path, file.Length, digest);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private sealed class BoundedWriteStream(Stream inner, long limit) : Stream
    {
        private long written;
        private long nextSpaceCheck;
        private void CheckSpace(int count)
        {
            if (written >= nextSpaceCheck)
            {
                if (AvailableSpace() < count) throw new InsufficientTransferStorageException();
                nextSpaceCheck = written + 16 * 1024 * 1024;
            }
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > limit - written) throw new InvalidDataException("Stored object exceeds its allowed size.");
            CheckSpace(count);
            inner.Write(buffer, offset, count);
            written += count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length > limit - written) throw new InvalidDataException("Stored object exceeds its allowed size.");
            CheckSpace(buffer.Length);
            await inner.WriteAsync(buffer, ct);
            written += buffer.Length;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!transferred) File.Delete(Path);
        return ValueTask.CompletedTask;
    }
}
