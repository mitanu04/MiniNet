using Microsoft.EntityFrameworkCore;
using MiniNet.Server.Data;
using MiniNet.Server.Hubs;
using MiniNet.Server.Network;
using MiniNet.Server.Network.Config;

var builder = WebApplication.CreateBuilder(args);

var networkConfig = builder.Configuration
    .GetSection("Network")
    .Get<NetworkTopologyConfig>() ?? new NetworkTopologyConfig();

builder.Services.AddSingleton(networkConfig);
builder.Services.AddSingleton<NetworkService>();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p =>
        p.SetIsOriginAllowed(_ => true)
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()));

builder.Services.AddDbContextFactory<NetworkDbContext>(options =>
    options.UseSqlite("Data Source=mininet.db"));

var app = builder.Build();

// Create DB tables if they don't exist and load persisted topology
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
await using (var db = await dbFactory.CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
    // Add missing columns to Switches if needed (migrations for existing DBs)
    try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Switches\" ADD COLUMN \"RouterName\" TEXT NOT NULL DEFAULT ''"); }
    catch { /* column already exists */ }
    try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Switches\" ADD COLUMN \"CreatedAt\" TEXT NOT NULL DEFAULT ''"); }
    catch { /* column already exists */ }

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Routers" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL DEFAULT '',
            "CreatedAt" TEXT NOT NULL DEFAULT ''
        )
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Events" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Timestamp" TEXT NOT NULL DEFAULT '',
            "EventType" TEXT NOT NULL DEFAULT '',
            "Description" TEXT NOT NULL DEFAULT '',
            "SrcDevice" TEXT NOT NULL DEFAULT '',
            "DstDevice" TEXT NOT NULL DEFAULT ''
        )
        """);
}

var network = app.Services.GetRequiredService<NetworkService>();
await network.LoadPersistedStateAsync();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<NetworkHub>("/networkHub");
app.MapGet("/api/topology", (NetworkService n) => n.GetTopology());
app.MapGet("/api/events", async (IDbContextFactory<NetworkDbContext> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    return await db.Events.OrderByDescending(e => e.Id).Take(200).ToListAsync();
});

app.Run();
