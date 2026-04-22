using MiniNet.Core;
using MiniNet.Core.Devices;
using MiniNet.Tests.Helpers;
using Xunit;

namespace MiniNet.Tests;

public class SwitchTests
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
    public void LearnsMacOnReceive()
    {
        var logger = new ListLogger();
        var (sw, a, b, _) = BuildTopology(logger);

        a.SendRaw("BB:BB:BB:BB:BB:BB", "hello");

        Assert.True(sw.MacTable.ContainsKey(new MacAddress("AA:AA:AA:AA:AA:AA")));
    }

    [Fact]
    public void ForwardsUnicastWhenDestinationMacKnown()
    {
        var logger = new ListLogger();
        var (sw, a, _, c) = BuildTopology(logger);

        // Teach the switch where C is
        c.SendRaw("AA:AA:AA:AA:AA:AA", "ping");
        logger.Messages.Clear();

        a.SendRaw("CC:CC:CC:CC:CC:CC", "hello");

        Assert.Contains(logger.Messages, m => m.Contains("Forwarding") && m.Contains("port 3"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Flooding"));
    }

    [Fact]
    public void FloodsWhenDestinationMacUnknown()
    {
        var logger = new ListLogger();
        var (_, a, _, _) = BuildTopology(logger);

        a.SendRaw("CC:CC:CC:CC:CC:CC", "hello");

        Assert.Contains(logger.Messages, m => m.Contains("Flooding"));
    }

    [Fact]
    public void FloodsAllPortsOnBroadcast()
    {
        var logger = new ListLogger();
        var (_, a, _, _) = BuildTopology(logger);

        a.SendRaw("FF:FF:FF:FF:FF:FF", "broadcast");

        var floodMessages = logger.Messages.Where(m => m.Contains("Flooding")).ToList();
        Assert.Equal(2, floodMessages.Count); // ports 2 and 3, not port 1 (sender)
    }

    [Fact]
    public void DoesNotFloodBackToSender()
    {
        var logger = new ListLogger();
        var (_, a, _, _) = BuildTopology(logger);

        a.SendRaw("FF:FF:FF:FF:FF:FF", "broadcast");

        Assert.DoesNotContain(logger.Messages, m => m.Contains("Flooding") && m.Contains("port 1"));
    }
}
