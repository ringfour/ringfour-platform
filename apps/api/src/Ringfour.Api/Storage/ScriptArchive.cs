using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ringfour.Api.Storage;

/// <summary>Validates uploaded ZIP bytes before they can become an immutable revision.</summary>
public static class ScriptArchive
{
    private const int MaxFileBytes = 262144;
    private const int MaxFiles = 1024;

    public static async Task VerifyAsync(
        byte[] archive, string archiveSha256, int archiveSize, string revisionDigest,
        string entrypoint, string lockSha256, CancellationToken cancellationToken)
    {
        if (archiveSize != archive.Length || archive.Length is < 1 or > ObjectStoreKey.MaxObjectBytes ||
            Sha256(archive) != archiveSha256 || !ValidPath(entrypoint) || !entrypoint.EndsWith(".py", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid archive size, digest or entrypoint.");

        using var bytes = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        var explicitPaths = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!ValidPath(path) || !explicitPaths.Add(path) ||
                (fileType != 0 && fileType != (directory ? 0x4000 : 0x8000)))
                throw new InvalidDataException("Invalid or duplicate ZIP entry.");
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var parent = string.Join('/', parts.Take(i));
                if (files.ContainsKey(parent)) throw new InvalidDataException("File conflicts with a directory.");
                directories.Add(parent);
            }
            if (directory)
            {
                if (files.ContainsKey(path)) throw new InvalidDataException("File conflicts with a directory.");
                directories.Add(path);
                continue;
            }
            if (directories.Contains(path) || files.Count >= MaxFiles || entry.Length > MaxFileBytes)
                throw new InvalidDataException("ZIP file count, path or size is invalid.");
            await using var source = entry.Open();
            var content = await ObjectStoreKey.ReadBoundedAsync(source, cancellationToken);
            total += content.Length;
            if (content.Length != entry.Length || content.Length > MaxFileBytes || total > ObjectStoreKey.MaxObjectBytes)
                throw new InvalidDataException("ZIP file exceeds the byte limit.");
            if (path == "requirements.lock")
            {
                try { _ = new UTF8Encoding(false, true).GetString(content); }
                catch (DecoderFallbackException exception) { throw new InvalidDataException("Lock must be UTF-8.", exception); }
            }
            files.Add(path, Sha256(content));
        }
        if (!files.ContainsKey(entrypoint) || !files.TryGetValue("requirements.lock", out var actualLockHash) || actualLockHash != lockSha256)
            throw new InvalidDataException("Entrypoint or lock is missing or mismatched.");
        var manifest = files.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new[] { item.Key, item.Value }).ToArray();
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (Sha256(Encoding.UTF8.GetBytes(json)) != revisionDigest)
            throw new InvalidDataException("Bundle digest mismatch.");
    }

    // New streaming path for large uploads; existing bounded callers retain their limits.
    public static async Task VerifyFileAsync(
        string archivePath, string archiveSha256, long archiveSize, string revisionDigest,
        string entrypoint, string lockSha256, long availableExpansionBytes, long maxEntryBytes, CancellationToken cancellationToken)
    {
        await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archiveSize < 1 || archive.Length != archiveSize ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, cancellationToken)) != archiveSha256 ||
            !ValidPath(entrypoint) || !entrypoint.EndsWith(".py", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid archive size, digest or entrypoint.");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxFiles * 2) throw new InvalidDataException("Too many ZIP entries.");
        var explicitPaths = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var buffer = new byte[65536];
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!ValidPath(path) || !explicitPaths.Add(path) ||
                (fileType != 0 && fileType != (directory ? 0x4000 : 0x8000)))
                throw new InvalidDataException("Invalid or duplicate ZIP entry.");
            var parts = path.Split('/');
            for (var index = 1; index < parts.Length; index++)
            {
                var parent = string.Join('/', parts.Take(index));
                if (files.ContainsKey(parent)) throw new InvalidDataException("File conflicts with a directory.");
                directories.Add(parent);
            }
            if (directory)
            {
                if (files.ContainsKey(path)) throw new InvalidDataException("File conflicts with a directory.");
                directories.Add(path);
                continue;
            }
            if (directories.Contains(path) || files.Count >= MaxFiles || entry.Length < 0 || entry.Length > maxEntryBytes)
                throw new InvalidDataException("ZIP has an invalid path or entry size.");
            if (entry.Length > availableExpansionBytes - total) throw new InsufficientTransferStorageException();
            await using var source = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var decoder = path == "requirements.lock" ? new UTF8Encoding(false, true).GetDecoder() : null;
            var chars = decoder is null ? null : new char[65536];
            long length = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                length += read;
                if (length > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared size.");
                if (read > availableExpansionBytes - total) throw new InsufficientTransferStorageException();
                total += read;
                hash.AppendData(buffer, 0, read);
                try { decoder?.GetChars(buffer, 0, read, chars!, 0, false); }
                catch (DecoderFallbackException exception) { throw new InvalidDataException("Lock must be UTF-8.", exception); }
            }
            if (length != entry.Length) throw new InvalidDataException("ZIP entry length does not match.");
            try { decoder?.GetChars([], 0, 0, chars!, 0, true); }
            catch (DecoderFallbackException exception) { throw new InvalidDataException("Lock must be UTF-8.", exception); }
            files.Add(path, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        if (!files.ContainsKey(entrypoint) || !files.TryGetValue("requirements.lock", out var actualLockHash) || actualLockHash != lockSha256)
            throw new InvalidDataException("Entrypoint or lock is missing or mismatched.");
        var manifest = files.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new[] { item.Key, item.Value }).ToArray();
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (Sha256(Encoding.UTF8.GetBytes(json)) != revisionDigest) throw new InvalidDataException("Bundle digest mismatch.");
    }

    private static bool ValidPath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 240 &&
        !path.StartsWith('/') && path.Split('/').All(part => !string.IsNullOrEmpty(part) && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
