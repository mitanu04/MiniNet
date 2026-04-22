namespace MiniNet.Server.Data;

public sealed class SwitchRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string RouterName { get; set; } = ""; // empty = no router (isolated)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
