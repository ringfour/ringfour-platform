using Microsoft.EntityFrameworkCore;
using Ringfour.Api;
using Ringfour.Api.Auth;
using Ringfour.Api.Errors;
using Ringfour.Api.Health;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = RingfourJson.Options.PropertyNamingPolicy;
    foreach (var converter in RingfourJson.Options.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

builder.Services.AddDbContext<RingfourDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.AddRingfourAuth();

var app = builder.Build();

AuthSetup.ValidateAuthConfiguration(app.Configuration, app.Environment);

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ErrorEnvelopeMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapIdentityEndpoints();
app.MapRobotEndpoints();
app.MapFallback(() =>
{
    throw ApiException.NotFound("The requested resource does not exist in this organization.");
});

await app.RunAsync();

public partial class Program;
