namespace MiniNet.Server.Models;

public sealed class NetworkEventDto
{
    public string EventType { get; set; } = "";  // DeviceConnected, DeviceDisconnected, ArpRequest,
                                                  // ArpReply, IcmpEchoRequest, IcmpEchoReply,
                                                  // FrameForwarded, FrameFlooded, PacketDropped, TtlExpired
    public string Description { get; set; } = "";
    public string? SrcDevice { get; set; }
    public string? DstDevice { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("HH:mm:ss.fff");
}
