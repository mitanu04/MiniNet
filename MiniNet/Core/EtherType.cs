namespace MiniNet.Core;

public enum EtherType : ushort
{
    IPv4 = 0x0800,
    ARP  = 0x0806,
    IPv6 = 0x86DD
}
