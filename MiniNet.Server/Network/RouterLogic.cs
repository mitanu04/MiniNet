using MiniNet.Server.Models;
using MiniNet.Server.Network.Config;

namespace MiniNet.Server.Network;

public sealed class RouterLogic
{
    public string Name { get; }

    private readonly RouterConfig _config;
    private readonly Dictionary<string, string> _arpTable = new(); // ip → mac

    public RouterLogic(RouterConfig config)
    {
        Name = config.Name;
        _config = config;
    }

    public void LearnHost(string ip, string mac) => _arpTable[ip] = mac;

    /// <summary>Attach a new interface at runtime (e.g. when a dynamic switch is created).</summary>
    public void AddInterface(RouterInterfaceConfig iface) => _config.Interfaces.Add(iface);

    public async Task ReceiveFrame(SendFrameDto frame, string interfaceName, IFrameFabric fabric)
    {
        var iface = _config.Interfaces.FirstOrDefault(i => i.Name == interfaceName);
        if (iface == null) return;

        if (frame.FrameType == "ARP" && frame.Arp != null)
        {
            await HandleArp(frame.Arp, fabric);
            return;
        }

        if (frame.FrameType == "IPv4" && frame.Ip != null)
        {
            await HandleIp(frame.Ip, frame.SrcMac, iface, fabric);
        }
    }

    private async Task HandleArp(ArpDto arp, IFrameFabric fabric)
    {
        _arpTable[arp.SenderIp] = arp.SenderMac;

        if (arp.Operation != "Request") return;

        var targetIface = _config.Interfaces.FirstOrDefault(i => i.Ip == arp.TargetIp);
        if (targetIface == null) return;

        await fabric.SendFrameOnSwitch(targetIface.Switch, new SendFrameDto
        {
            FrameType = "ARP",
            SrcMac = targetIface.Mac,
            DstMac = arp.SenderMac,
            Arp = new ArpDto
            {
                Operation = "Reply",
                SenderMac = targetIface.Mac,
                SenderIp = targetIface.Ip,
                TargetMac = arp.SenderMac,
                TargetIp = arp.SenderIp
            }
        });

        await fabric.BroadcastEvent(new NetworkEventDto
        {
            EventType = "ArpReply",
            Description = $"{Name} ({targetIface.Ip}) → ARP reply to {arp.SenderIp}",
            SrcDevice = Name,
            DstDevice = arp.SenderIp
        });
    }

    private async Task HandleIp(IpDto ip, string senderMac, RouterInterfaceConfig iface, IFrameFabric fabric)
    {
        if (_config.Interfaces.Any(i => i.Ip == ip.DstIp))
            return; // packet addressed to us — drop silently (we're not a full IP stack)

        if (ip.Ttl <= 1)
        {
            // Send ICMP Time Exceeded back to the source
            await fabric.SendFrameOnSwitch(iface.Switch, new SendFrameDto
            {
                FrameType = "IPv4",
                SrcMac    = iface.Mac,
                DstMac    = senderMac,
                Ip = new IpDto
                {
                    SrcIp    = iface.Ip,
                    DstIp    = ip.SrcIp,
                    Protocol = "ICMP",
                    Ttl      = 64,
                    Icmp = new IcmpDto
                    {
                        Type           = "TimeExceeded",
                        Identifier     = ip.Icmp?.Identifier     ?? 0,
                        SequenceNumber = ip.Icmp?.SequenceNumber ?? 0
                    }
                }
            });

            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType   = "TtlExpired",
                Description = $"{Name} ({iface.Ip}): TTL expired — {ip.SrcIp} → {ip.DstIp}",
                SrcDevice   = ip.SrcIp,
                DstDevice   = ip.DstIp
            });
            return;
        }

        var route = FindRoute(ip.DstIp);
        if (route == null)
        {
            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType = "PacketDropped",
                Description = $"{Name}: no route to {ip.DstIp}",
                SrcDevice = ip.SrcIp,
                DstDevice = ip.DstIp
            });
            return;
        }

        var (outInterface, nextHop) = route.Value;

        if (!_arpTable.TryGetValue(nextHop, out var nextHopMac))
        {
            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType = "PacketDropped",
                Description = $"{Name}: ARP miss for next hop {nextHop} (is {nextHop} connected?)",
                SrcDevice = ip.SrcIp,
                DstDevice = ip.DstIp
            });
            return;
        }

        var eventType = ip.Protocol == "TCP"
            ? ip.Tcp switch
            {
                { Syn: true, Ack: false } => "TcpSyn",
                { Syn: true, Ack: true  } => "TcpSynAck",
                { Fin: true }             => "TcpFin",
                _                         => "FrameForwarded"
            }
            : ip.Icmp?.Type switch
            {
                "EchoRequest" => "IcmpEchoRequest",
                "EchoReply"   => "IcmpEchoReply",
                _              => "FrameForwarded"
            };

        await fabric.SendFrameOnSwitch(outInterface.Switch, new SendFrameDto
        {
            FrameType = "IPv4",
            SrcMac = outInterface.Mac,
            DstMac = nextHopMac,
            Ip = new IpDto
            {
                SrcIp = ip.SrcIp,
                DstIp = ip.DstIp,
                Protocol = ip.Protocol,
                Ttl = ip.Ttl - 1,
                Icmp = ip.Icmp,
                Tcp = ip.Tcp
            }
        });

        await fabric.BroadcastEvent(new NetworkEventDto
        {
            EventType = eventType,
            Description = $"{Name}: {ip.SrcIp} → {ip.DstIp} via {outInterface.Name} (TTL {ip.Ttl} → {ip.Ttl - 1})",
            SrcDevice = ip.SrcIp,
            DstDevice = ip.DstIp
        });
    }

    private (RouterInterfaceConfig OutInterface, string NextHop)? FindRoute(string destIp)
    {
        // Directly connected networks (implicit routes from interface config)
        foreach (var iface in _config.Interfaces)
        {
            if (IsInSubnet(destIp, iface.Ip, iface.PrefixLength))
                return (iface, destIp);
        }

        // Static routes — longest prefix first
        foreach (var route in _config.Routes.OrderByDescending(r => r.PrefixLength))
        {
            if (IsInSubnet(destIp, route.Destination, route.PrefixLength))
            {
                var outIface = _config.Interfaces
                    .FirstOrDefault(i => IsInSubnet(route.Via, i.Ip, i.PrefixLength));
                if (outIface != null)
                    return (outIface, route.Via);
            }
        }

        return null;
    }

    private static bool IsInSubnet(string ip, string network, int prefix)
    {
        uint mask = prefix == 0 ? 0u : ~0u << (32 - prefix);
        return (ToUInt32(ip) & mask) == (ToUInt32(network) & mask);
    }

    private static uint ToUInt32(string ip)
    {
        var p = ip.Split('.');
        return (uint.Parse(p[0]) << 24) | (uint.Parse(p[1]) << 16) |
               (uint.Parse(p[2]) << 8)  |  uint.Parse(p[3]);
    }
}
