using MiniNet.Server.Models;

namespace MiniNet.Server.Network;

public sealed class SimulatedHost
{
    public string Name       { get; }
    public string Mac        { get; }
    public string Ip         { get; }
    public int    PrefixLength { get; }
    public string Gateway    { get; }
    public string SwitchName { get; }

    // Active TCP sessions: (srcIp, srcPort, dstPort) → next expected seq
    private readonly Dictionary<(string, int, int), int> _tcpSessions = new();
    private readonly Random _rng = new();

    public SimulatedHost(string name, string mac, string ip, int prefix, string gateway, string sw)
    {
        Name = name; Mac = mac; Ip = ip; PrefixLength = prefix; Gateway = gateway; SwitchName = sw;
    }

    public async Task HandleFrame(SendFrameDto frame, IFrameFabric fabric)
    {
        if (frame.DstMac != "FF:FF:FF:FF:FF:FF" && frame.DstMac != Mac) return;

        if (frame.FrameType == "ARP" && frame.Arp != null)
        {
            await HandleArp(frame.Arp, fabric);
            return;
        }

        if (frame.FrameType == "IPv4" && frame.Ip != null)
            await HandleIp(frame.Ip, frame.SrcMac, fabric);
    }

    // ── ARP ──────────────────────────────────────────────────────────────────

    private async Task HandleArp(ArpDto arp, IFrameFabric fabric)
    {
        if (arp.Operation != "Request" || arp.TargetIp != Ip) return;

        await fabric.SendFrameOnSwitch(SwitchName, new SendFrameDto
        {
            FrameType = "ARP", SrcMac = Mac, DstMac = arp.SenderMac,
            Arp = new ArpDto
            {
                Operation = "Reply", SenderMac = Mac, SenderIp = Ip,
                TargetMac = arp.SenderMac, TargetIp = arp.SenderIp
            }
        });

        await fabric.BroadcastEvent(new NetworkEventDto
        {
            EventType = "ArpReply",
            Description = $"{Name} ({Ip}) → ARP reply to {arp.SenderIp}",
            SrcDevice = Name, DstDevice = arp.SenderIp
        });
    }

    // ── IP ────────────────────────────────────────────────────────────────────

    private async Task HandleIp(IpDto ip, string senderMac, IFrameFabric fabric)
    {
        if (ip.DstIp != Ip) return;

        if (ip.Protocol == "ICMP" && ip.Icmp?.Type == "EchoRequest")
            await HandleIcmp(ip, senderMac, fabric);
        else if (ip.Protocol == "TCP" && ip.Tcp != null)
            await HandleTcp(ip, senderMac, fabric);
    }

    // ── ICMP ──────────────────────────────────────────────────────────────────

    private async Task HandleIcmp(IpDto ip, string senderMac, IFrameFabric fabric)
    {
        await fabric.SendFrameOnSwitch(SwitchName, new SendFrameDto
        {
            FrameType = "IPv4", SrcMac = Mac, DstMac = senderMac,
            Ip = new IpDto
            {
                SrcIp = Ip, DstIp = ip.SrcIp, Protocol = "ICMP", Ttl = 64,
                Icmp = new IcmpDto
                {
                    Type = "EchoReply",
                    Identifier = ip.Icmp!.Identifier,
                    SequenceNumber = ip.Icmp.SequenceNumber,
                    Payload = ip.Icmp.Payload
                }
            }
        });

        await fabric.BroadcastEvent(new NetworkEventDto
        {
            EventType = "IcmpEchoReply",
            Description = $"{Name}: pong {Ip} → {ip.SrcIp} seq={ip.Icmp!.SequenceNumber}",
            SrcDevice = Name, DstDevice = ip.SrcIp
        });
    }

    // ── TCP ───────────────────────────────────────────────────────────────────

    private async Task HandleTcp(IpDto ip, string senderMac, IFrameFabric fabric)
    {
        var tcp = ip.Tcp!;
        var sessionKey = (ip.SrcIp, tcp.SrcPort, tcp.DstPort);

        // SYN → SYN-ACK (three-way handshake step 2)
        if (tcp.Syn && !tcp.Ack)
        {
            var serverSeq = _rng.Next(1000, 9999);
            _tcpSessions[sessionKey] = tcp.SequenceNumber + 1;

            await SendTcp(fabric, ip.SrcIp, senderMac, tcp.DstPort, tcp.SrcPort,
                syn: true, ack: true, seq: serverSeq, ackNum: tcp.SequenceNumber + 1);

            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType = "TcpSynAck",
                Description = $"{Name}:{tcp.DstPort} → SYN-ACK → {ip.SrcIp}:{tcp.SrcPort} (seq={serverSeq}, ack={tcp.SequenceNumber + 1})",
                SrcDevice = Name, DstDevice = ip.SrcIp
            });
            return;
        }

        // ACK only → handshake complete
        if (!tcp.Syn && tcp.Ack && !tcp.Fin && string.IsNullOrEmpty(tcp.Payload))
        {
            if (_tcpSessions.ContainsKey(sessionKey))
                await fabric.BroadcastEvent(new NetworkEventDto
                {
                    EventType = "TcpAck",
                    Description = $"{Name}:{tcp.DstPort} ← ACK from {ip.SrcIp}:{tcp.SrcPort} — connection ESTABLISHED",
                    SrcDevice = Name, DstDevice = ip.SrcIp
                });
            return;
        }

        // DATA → ACK (step 5,7,10)
        if (!string.IsNullOrEmpty(tcp.Payload))
        {
            var expectedSeq = _tcpSessions.GetValueOrDefault(sessionKey, 0);
            var nextSeq = tcp.SequenceNumber + tcp.PayloadSize;
            _tcpSessions[sessionKey] = nextSeq;

            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType = "TcpData",
                Description = $"{Name}:{tcp.DstPort} ← DATA from {ip.SrcIp}:{tcp.SrcPort} [{tcp.SequenceNumber}-{nextSeq - 1}] \"{tcp.Payload}\"",
                SrcDevice = Name, DstDevice = ip.SrcIp
            });

            await SendTcp(fabric, ip.SrcIp, senderMac, tcp.DstPort, tcp.SrcPort,
                ack: true, seq: expectedSeq, ackNum: nextSeq);

            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType = "TcpAck",
                Description = $"{Name}:{tcp.DstPort} → ACK → {ip.SrcIp}:{tcp.SrcPort} (ack={nextSeq})",
                SrcDevice = Name, DstDevice = ip.SrcIp
            });
            return;
        }

        // FIN → FIN-ACK (teardown)
        if (tcp.Fin)
        {
            var expectedSeq = _tcpSessions.GetValueOrDefault(sessionKey, 0);
            _tcpSessions.Remove(sessionKey);

            await SendTcp(fabric, ip.SrcIp, senderMac, tcp.DstPort, tcp.SrcPort,
                fin: true, ack: true, seq: expectedSeq, ackNum: tcp.SequenceNumber + 1);

            await fabric.BroadcastEvent(new NetworkEventDto
            {
                EventType = "TcpFin",
                Description = $"{Name}:{tcp.DstPort} → FIN-ACK → {ip.SrcIp}:{tcp.SrcPort} — connection CLOSED",
                SrcDevice = Name, DstDevice = ip.SrcIp
            });
        }
    }

    private Task SendTcp(IFrameFabric fabric, string dstIp, string dstMac,
        int srcPort, int dstPort,
        bool syn = false, bool ack = false, bool fin = false,
        int seq = 0, int ackNum = 0)
    {
        return fabric.SendFrameOnSwitch(SwitchName, new SendFrameDto
        {
            FrameType = "IPv4", SrcMac = Mac, DstMac = dstMac,
            Ip = new IpDto
            {
                SrcIp = Ip, DstIp = dstIp, Protocol = "TCP", Ttl = 64,
                Tcp = new TcpDto
                {
                    SrcPort = srcPort, DstPort = dstPort,
                    Syn = syn, Ack = ack, Fin = fin,
                    SequenceNumber = seq, AckNumber = ackNum
                }
            }
        });
    }
}
