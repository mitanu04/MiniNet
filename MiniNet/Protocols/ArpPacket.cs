using MiniNet.Core;

namespace MiniNet.Core.Protocols;

public enum ArpOperation : ushort { Request = 1, Reply = 2 }

public sealed class ArpPacket
{
    public ArpOperation Operation { get; }
    public MacAddress SenderMac { get; }
    public IpAddress SenderIp { get; }
    public MacAddress TargetMac { get; }
    public IpAddress TargetIp { get; }

    public ArpPacket(ArpOperation operation, MacAddress senderMac, IpAddress senderIp,
                     MacAddress targetMac, IpAddress targetIp)
    {
        Operation = operation;
        SenderMac = senderMac;
        SenderIp = senderIp;
        TargetMac = targetMac;
        TargetIp = targetIp;
    }

    public override string ToString() =>
        $"[ARP {Operation}] {SenderIp} ({SenderMac}) → {TargetIp} ({TargetMac})";
}
