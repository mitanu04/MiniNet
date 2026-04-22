using Microsoft.EntityFrameworkCore;

namespace MiniNet.Server.Data;

public sealed class NetworkDbContext(DbContextOptions<NetworkDbContext> options) : DbContext(options)
{
    public DbSet<RouterRecord> Routers  { get; set; }
    public DbSet<SwitchRecord> Switches { get; set; }
    public DbSet<DeviceRecord> Devices  { get; set; }
    public DbSet<EventRecord>  Events   { get; set; }
}
