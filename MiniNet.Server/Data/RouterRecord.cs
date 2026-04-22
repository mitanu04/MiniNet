namespace MiniNet.Server.Data;

public sealed class RouterRecord
{
    public int    Id        { get; set; }
    public string Name      { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
