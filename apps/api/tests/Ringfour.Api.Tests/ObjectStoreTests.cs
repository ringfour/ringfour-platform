using Ringfour.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ringfour.Api.Tests;

public class ObjectStoreTests
{
    [Fact]
    public void ConfigurationRequiresExplicitValidBackend()
    {
        using var missing = new ServiceCollection().AddRingfourStorage(new ConfigurationBuilder().Build()).BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => missing.GetRequiredService<IObjectStore>());
        var options = new Dictionary<string, string?> { ["Storage:Backend"] = "filesystem", ["Storage:FilesystemRoot"] = Path.GetTempPath() };
        using var filesystem = new ServiceCollection().AddRingfourStorage(new ConfigurationBuilder().AddInMemoryCollection(options).Build()).BuildServiceProvider();
        Assert.IsType<FileObjectStore>(filesystem.GetRequiredService<IObjectStore>());
        options = new Dictionary<string, string?> { ["Storage:Backend"] = "s3", ["Storage:S3:Endpoint"] = "http://user:secret@localhost", ["Storage:S3:Region"] = "garage", ["Storage:S3:Bucket"] = "test" };
        using var badS3 = new ServiceCollection().AddRingfourStorage(new ConfigurationBuilder().AddInMemoryCollection(options).Build()).BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => badS3.GetRequiredService<IObjectStore>());
    }

    [Fact]
    public async Task FilesystemRoundTripAndIdempotentDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "ringfour-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            IObjectStore store = new FileObjectStore(root);
            var data = "hello"u8.ToArray();
            await store.PutAsync("uploads/abc", data, CancellationToken.None);
            Assert.Equal(data, await store.GetAsync("uploads/abc", CancellationToken.None));
            await store.DeleteAsync("uploads/abc", CancellationToken.None);
            await store.DeleteAsync("uploads/abc", CancellationToken.None);
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.GetAsync("uploads/abc", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a/./b")]
    public async Task RejectsInvalidKeys(string key)
    {
        IObjectStore store = new FileObjectStore(Path.GetTempPath());
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(key, [], CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedObjects()
    {
        IObjectStore store = new FileObjectStore(Path.GetTempPath());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutAsync("x", new byte[1048577], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ObjectStoreKey.ReadBoundedAsync(new MemoryStream(new byte[1048577]), CancellationToken.None));
    }
}
