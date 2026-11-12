using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Ringfour.Api;
using Ringfour.Api.Auth;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Health;
using Ringfour.Api.Identity;
using Ringfour.Api.Missions;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

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

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.Connection.RemoteIpAddress}:{(context.Request.Path.Value ?? "").Contains("/poll")}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        await context.HttpContext.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(
            "rate_limited", "Too many pairing requests.", context.HttpContext.TraceIdentifier)), ct);
    };
});
builder.Services.AddSingleton<DeviceConnectionManager>();
builder.Services.AddSingleton<ConnectivityMetrics>();
builder.Services.AddHostedService<OperationSweeper>();
builder.Services.AddHostedService<DeploymentLifecycleWorker>();
builder.Services.AddRingfourStorage(builder.Configuration);
builder.Services.AddHostedService<ScriptStorageSweeper>();

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
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (context.Request.Method == "POST" &&
        (path.StartsWith("/v1/device/pairing-requests", StringComparison.Ordinal) ||
         path.EndsWith("/pairing-claims", StringComparison.Ordinal)))
    {
        var buffer = new byte[4097];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(total));
            if (read == 0) break;
            total += read;
        }
        if (total > 4096)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(
                "size_limit", "Pairing request exceeds 4096 bytes.", context.TraceIdentifier)));
            return;
        }
        context.Request.Body = new MemoryStream(buffer, 0, total);
    }
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();

app.MapHealthEndpoints();
app.MapIdentityEndpoints();
app.MapRobotEndpoints();
app.MapPairingEndpoints();
app.MapOperationEndpoints();
app.MapScriptRevisionEndpoints();
app.MapScriptRunEndpoints();
app.MapMissionEndpoints();
app.MapDeploymentEndpoints();
app.MapDeploymentLifecycle();
app.MapDeviceExecutionEndpoints();
app.MapDeviceSocketEndpoint();
app.MapFallback(() =>
{
    throw ApiException.NotFound("The requested resource does not exist in this organization.");
});

await app.RunAsync();

public partial class Program;
