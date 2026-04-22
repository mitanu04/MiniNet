using MiniNet.Core;
using MiniNet.Core.Devices;
using MiniNet.Tests.Helpers;
using Xunit;

namespace MiniNet.Tests;

public class HostTests
{
    private static (Switch sw, Host a, Host b, Host c) BuildTopology(ListLogger logger)
    {
        var sw = new Switch("SW", logger);
        var a = new Host("A", "AA:AA:AA:AA:AA:AA", "10.0.0.1", logger);
        var b = new Host("B", "BB:BB:BB:BB:BB:BB", "10.0.0.2", logger);
        var c = new Host("C", "CC:CC:CC:CC:CC:CC", "10.0.0.3", logger);
        a.Link = sw.Connect(a);
        b.Link = sw.Connect(b);
        c.Link = sw.Connect(c);
        return (sw, a, b, c);
    }

    [Fact]
    public void DropsUnicastFrameNotAddressedToIt()
    {
        var logger = new ListLogger();
        var (_, a, b, _) = BuildTopology(logger);

        // A sends to B; during flood (B's MAC unknown), C should drop the frame
        a.SendRaw("BB:BB:BB:BB:BB:BB", "for B only");

        Assert.DoesNotContain(logger.Messages, m => m.Contains("[C] Received"));
    }

    [Fact]
    public void ReceivesBroadcastFrame()
    {
        var logger = new ListLogger();
        var (_, a, _, _) = BuildTopology(logger);

        a.SendRaw("FF:FF:FF:FF:FF:FF", "hello everyone");

        Assert.Contains(logger.Messages, m => m.Contains("[B] Received"));
        Assert.Contains(logger.Messages, m => m.Contains("[C] Received"));
    }

    [Fact]
    public void ArpResolvesIpToMac()
    {
        var logger = new ListLogger();
        var (_, a, _, c) = BuildTopology(logger);

        a.SendIp("10.0.0.3", "hello C");

        Assert.True(a.ArpTable.ContainsKey(new IpAddress("10.0.0.3")));
        Assert.Equal(new MacAddress("CC:CC:CC:CC:CC:CC"), a.ArpTable[new IpAddress("10.0.0.3")]);
    }

    [Fact]
    public void ArpReplyLearnsSenderMac()
    {
        var logger = new ListLogger();
        var (_, a, _, c) = BuildTopology(logger);

        a.SendIp("10.0.0.3", "hello");

        // C should have learned A's IP→MAC from the ARP request
        Assert.True(c.ArpTable.ContainsKey(new IpAddress("10.0.0.1")));
    }

    [Fact]
    public void SendIpDeliverMessageAfterArp()
    {
        var logger = new ListLogger();
        var (_, a, _, _) = BuildTopology(logger);

        a.SendIp("10.0.0.3", "hello C via IP");

        Assert.Contains(logger.Messages, m => m.Contains("[C] Received") && m.Contains("hello C via IP"));
    }

    [Fact]
    public void InvalidMacThrows()
    {
        Assert.Throws<ArgumentException>(() => new MacAddress("not-a-mac"));
    }

    [Fact]
    public void InvalidIpThrows()
    {
        Assert.Throws<ArgumentException>(() => new IpAddress("999.0.0.1"));
    }
}
