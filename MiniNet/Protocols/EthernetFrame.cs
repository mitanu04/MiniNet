using MiniNet.Core;

namespace MiniNet.Core.Protocols;

public sealed class EthernetFrame
{
    public MacAddress Source { get; }
    public MacAddress Destination { get; }
    public EtherType EtherType { get; }

    private readonly object _payload;

    public EthernetFrame(MacAddress src, MacAddress dest, byte[] data, EtherType etherType = EtherType.IPv4)
    {
        Source = src;
        Destination = dest;
        EtherType = etherType;
        _payload = data;
    }

    public EthernetFrame(MacAddress src, MacAddress dest, ArpPacket arp)
    {
        Source = src;
        Destination = dest;
        EtherType = EtherType.ARP;
        _payload = arp;
    }

    public byte[] GetData() => (byte[])_payload;
    public ArpPacket GetArp() => (ArpPacket)_payload;

    public override string ToString()
    {
        var size = EtherType == EtherType.ARP ? "ARP" : $"{GetData().Length} bytes";
        return $"[Ethernet] {Source} → {Destination} | {EtherType} | {size}";
    }
}
