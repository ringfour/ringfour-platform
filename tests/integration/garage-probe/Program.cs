using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Ringfour.Api.Storage;

var endpoint = Environment.GetEnvironmentVariable("GARAGE_TEST_ENDPOINT")
    ?? throw new InvalidOperationException("GARAGE_TEST_ENDPOINT is required");
var access = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
    ?? throw new InvalidOperationException("AWS_ACCESS_KEY_ID is required");
var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")
    ?? throw new InvalidOperationException("AWS_SECRET_ACCESS_KEY is required");
using var client = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
{
    ServiceURL = endpoint,
    AuthenticationRegion = "garage",
    ForcePathStyle = true,
});
IObjectStore store = new S3ObjectStore(client, "ringfour-test");
var data = "Ringfour Garage adapter"u8.ToArray();
await store.PutAsync("integration/adapter", data, CancellationToken.None);
var retrieved = await store.GetAsync("integration/adapter", CancellationToken.None);
if (!data.SequenceEqual(retrieved))
    throw new InvalidOperationException("Stored bytes differ");
await store.DeleteAsync("integration/adapter", CancellationToken.None);
await store.DeleteAsync("integration/adapter", CancellationToken.None);
Console.WriteLine("PASS Ringfour S3 adapter PUT/GET/delete against Garage");
var interrupted = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = "ringfour-test", Key = "incoming/" + Guid.NewGuid().ToString("N") });
var unrelated = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = "ringfour-test", Key = "external/cleanup-test" });
await ((S3ObjectStore)store).CleanupInterruptedAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
var remaining = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = "ringfour-test" });
if ((remaining.MultipartUploads ?? []).Any(item => item.UploadId == interrupted.UploadId) ||
    !(remaining.MultipartUploads ?? []).Any(item => item.UploadId == unrelated.UploadId))
    throw new InvalidOperationException("Interrupted multipart cleanup was not scoped correctly");
await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = "ringfour-test", Key = "external/cleanup-test", UploadId = unrelated.UploadId });
Console.WriteLine("PASS stale Ringfour multipart cleanup against Garage");
var sourcePath = Path.GetTempFileName();
var destinationPath = Path.GetTempFileName();
try
{
    var chunk = new byte[65536];
    await using (var source = File.Create(sourcePath))
    {
        for (var index = 0; index < 272; index++)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(chunk);
            await source.WriteAsync(chunk);
        }
    }
    await store.PutFileAsync("integration/multipart", sourcePath, CancellationToken.None);
    await using (var destination = File.Create(destinationPath))
        await store.CopyToAsync("integration/multipart", destination, CancellationToken.None);
    await using var expected = File.OpenRead(sourcePath);
    await using var actual = File.OpenRead(destinationPath);
    if (!System.Security.Cryptography.SHA256.HashData(expected).SequenceEqual(System.Security.Cryptography.SHA256.HashData(actual)))
        throw new InvalidOperationException("Streamed bytes differ");
    await store.DeleteAsync("integration/multipart", CancellationToken.None);
    Console.WriteLine("PASS Ringfour S3 adapter streamed multipart PUT/GET against Garage");
}
finally
{
    File.Delete(sourcePath);
    File.Delete(destinationPath);
}
