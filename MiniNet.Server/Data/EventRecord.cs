namespace MiniNet.Server.Data;

public sealed class EventRecord
{
    public int    Id          { get; set; }
    public string Timestamp   { get; set; } = "";
    public string EventType   { get; set; } = "";
    public string Description { get; set; } = "";
    public string SrcDevice   { get; set; } = "";
    public string DstDevice   { get; set; } = "";
}
