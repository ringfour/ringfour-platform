using Amazon.Runtime;
using Amazon.S3;
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
