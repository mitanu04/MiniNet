namespace MiniNet.Server.Models;

public sealed class SendFrameDto
{
    public string FrameType { get; set; } = ""; // "ARP" | "IPv4"
    public string SrcMac { get; set; } = "";
    public string DstMac { get; set; } = "";
    public ArpDto? Arp { get; set; }
    public IpDto? Ip { get; set; }
}

public sealed class ArpDto
{
    public string Operation { get; set; } = ""; // "Request" | "Reply"
    public string SenderMac { get; set; } = "";
    public string SenderIp { get; set; } = "";
    public string TargetMac { get; set; } = "";
    public string TargetIp { get; set; } = "";
}

public sealed class IpDto
{
    public string SrcIp { get; set; } = "";
    public string DstIp { get; set; } = "";
    public string Protocol { get; set; } = ""; // "ICMP" | "TCP"
    public int Ttl { get; set; } = 64;
    public IcmpDto? Icmp { get; set; }
    public TcpDto? Tcp { get; set; }
}

public sealed class TcpDto
{
    public int SrcPort { get; set; }
    public int DstPort { get; set; }
    // Flags
    public bool Syn { get; set; }
    public bool Ack { get; set; }
    public bool Fin { get; set; }
    public bool Rst { get; set; }
    // Sequence / acknowledgement
    public int SequenceNumber { get; set; }
    public int AckNumber { get; set; }
    // Data
    public string? Payload { get; set; }
    public int PayloadSize { get; set; }

    public string FlagsString =>
        string.Join("-", new[] { Syn?"SYN":null, Ack?"ACK":null, Fin?"FIN":null, Rst?"RST":null }
            .Where(f => f != null));
}

public sealed class IcmpDto
{
    public string Type { get; set; } = ""; // "EchoRequest" | "EchoReply"
    public int Identifier { get; set; }
    public int SequenceNumber { get; set; }
    public string Payload { get; set; } = "";
}
