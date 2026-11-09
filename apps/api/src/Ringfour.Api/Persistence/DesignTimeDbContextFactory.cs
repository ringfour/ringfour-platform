using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ringfour.Api.Persistence;

// Used by dotnet-ef so design-time commands never run Program.cs or need a live database.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RingfourDbContext>
{
    public RingfourDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=ringfour;Username=ringfour;Password=ringfour_dev";
        var options = new DbContextOptionsBuilder<RingfourDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new RingfourDbContext(options);
    }
}
