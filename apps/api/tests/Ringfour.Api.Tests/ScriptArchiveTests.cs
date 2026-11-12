using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Tests;

public class ScriptArchiveTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Archive(params (string Name, byte[] Content, int Attributes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, attributes) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (attributes != 0) entry.ExternalAttributes = attributes;
                using var output = entry.Open();
                output.Write(content);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public async Task ValidatesRealArchiveAndCanonicalDigest()
    {
        var main = "print('hello')\n"u8.ToArray();
        var lockfile = Array.Empty<byte>();
        var archive = Archive(("main.py", main, 0), ("requirements.lock", lockfile, 0));
        var manifest = $"[[\"main.py\",\"{Hash(main)}\"],[\"requirements.lock\",\"{Hash(lockfile)}\"]]";
        var digest = Hash(Encoding.UTF8.GetBytes(manifest));
        await ScriptArchive.VerifyAsync(archive, Hash(archive), archive.Length, digest, "main.py", Hash(lockfile), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptArchive.VerifyAsync(archive, Hash(archive), archive.Length, new string('0', 64), "main.py", Hash(lockfile), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptArchive.VerifyAsync(archive, Hash(archive), archive.Length, digest, "main.py", new string('0', 64), CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("dir\\escape")]
    [InlineData("dir//escape")]
    public async Task RejectsUnsafePaths(string unsafeName)
    {
        var archive = Archive((unsafeName, "x"u8.ToArray(), 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptArchive.VerifyAsync(archive, Hash(archive), archive.Length, new string('0', 64), "main.py", new string('0', 64), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsSymlinksAndOversizedEntries()
    {
        var link = Archive(("main.py", "target"u8.ToArray(), 0xA000 << 16));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptArchive.VerifyAsync(link, Hash(link), link.Length, new string('0', 64), "main.py", new string('0', 64), CancellationToken.None));
        var oversized = Archive(("main.py", new byte[262145], 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptArchive.VerifyAsync(oversized, Hash(oversized), oversized.Length, new string('0', 64), "main.py", new string('0', 64), CancellationToken.None));
    }
}
