using Amazon.S3;

namespace Ringfour.Api.Storage;

public static class StorageSetup
{
    public static IServiceCollection AddRingfourStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IObjectStore>(_ =>
        {
            var backend = configuration["Storage:Backend"];
            if (string.Equals(backend, "filesystem", StringComparison.OrdinalIgnoreCase))
            {
                var root = configuration["Storage:FilesystemRoot"];
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                    throw new InvalidOperationException("Storage:FilesystemRoot must be an absolute path.");
                return new FileObjectStore(root);
            }
            if (string.Equals(backend, "s3", StringComparison.OrdinalIgnoreCase))
            {
                var endpoint = configuration["Storage:S3:Endpoint"];
                var region = configuration["Storage:S3:Region"];
                var bucket = configuration["Storage:S3:Bucket"];
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                    string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(bucket))
                    throw new InvalidOperationException("Storage:S3 requires endpoint, region and bucket.");
                var client = new AmazonS3Client(new AmazonS3Config
                {
                    ServiceURL = endpoint,
                    AuthenticationRegion = region,
                    ForcePathStyle = true,
                });
                return new S3ObjectStore(client, bucket);
            }
            throw new InvalidOperationException("Storage:Backend must be filesystem or s3.");
        });
        return services;
    }
}
