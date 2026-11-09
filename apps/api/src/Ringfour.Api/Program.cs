using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Health;
using Ringfour.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<RingfourDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
    await db.Database.MigrateAsync();
}

app.MapHealthEndpoints();

await app.RunAsync();

public partial class Program;
