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

    private static bool ValidPath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 240 &&
        !path.StartsWith('/') && path.Split('/').All(part => !string.IsNullOrEmpty(part) && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
