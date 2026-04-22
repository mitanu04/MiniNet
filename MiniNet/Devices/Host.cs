using System.Text;
using MiniNet.Core;
using MiniNet.Core.Protocols;
using MiniNet.Logging;
using MiniNet.Network;

namespace MiniNet.Core.Devices;

public sealed class Host : INetworkDevice
{
    public string Name { get; }
    public MacAddress MacAddress { get; }
    public IpAddress IpAddress { get; }

    public Link? Link { get; set; }

    private readonly Dictionary<IpAddress, MacAddress> _arpTable = new();
    private readonly ILogger _logger;

    public IReadOnlyDictionary<IpAddress, MacAddress> ArpTable => _arpTable;

    public Host(string name, MacAddress mac, IpAddress ip, ILogger? logger = null)
    {
        Name = name;
        MacAddress = mac;
        IpAddress = ip;
        _logger = logger ?? new ConsoleLogger();
    }

    public void Receive(EthernetFrame frame)
    {
        if (frame.Destination != MacAddress && !frame.Destination.IsBroadcast)
            return;

        if (frame.EtherType == EtherType.ARP)
        {
            HandleArp(frame.GetArp());
            return;
        }

        var message = Encoding.UTF8.GetString(frame.GetData());
        _logger.Log($"[{Name}] Received: \"{message}\" from {frame.Source}");
    }

    public void SendIp(IpAddress destIp, string message)
    {
        if (!_arpTable.TryGetValue(destIp, out var destMac))
        {
            SendArpRequest(destIp);

            if (!_arpTable.TryGetValue(destIp, out destMac))
            {
                _logger.Log($"[{Name}] ARP failed for {destIp}, dropping packet");
                return;
            }
        }

        var payload = Encoding.UTF8.GetBytes(message);
        Send(new EthernetFrame(MacAddress, destMac, payload));
    }

    public void SendRaw(MacAddress destMac, string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        Send(new EthernetFrame(MacAddress, destMac, payload));
    }

    private void SendArpRequest(IpAddress targetIp)
    {
        _logger.Log($"[{Name}] ARP Request: who has {targetIp}?");
        var arp = new ArpPacket(ArpOperation.Request, MacAddress, IpAddress, MacAddress.Broadcast, targetIp);
        Send(new EthernetFrame(MacAddress, MacAddress.Broadcast, arp));
    }

    private void HandleArp(ArpPacket arp)
    {
        _arpTable[arp.SenderIp] = arp.SenderMac;

        if (arp.Operation == ArpOperation.Request && arp.TargetIp == IpAddress)
        {
            _logger.Log($"[{Name}] ARP Reply: {IpAddress} is at {MacAddress}");
            var reply = new ArpPacket(ArpOperation.Reply, MacAddress, IpAddress, arp.SenderMac, arp.SenderIp);
            Send(new EthernetFrame(MacAddress, arp.SenderMac, reply));
        }
    }

    private void Send(EthernetFrame frame) => Link?.Deliver(frame, this);
}
