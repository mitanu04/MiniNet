namespace MiniNet.Server.Data;

public sealed class DeviceRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public int PrefixLength { get; set; }
    public string Gateway { get; set; } = "";
    public string SwitchName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
