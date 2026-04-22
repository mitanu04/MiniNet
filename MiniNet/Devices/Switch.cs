using MiniNet.Core;
using MiniNet.Core.Protocols;
using MiniNet.Logging;
using MiniNet.Network;

namespace MiniNet.Core.Devices;

public sealed class Switch
{
    private sealed class SwitchPort : INetworkDevice
    {
        public int Number { get; }
        public string Name { get; }
        public MacAddress MacAddress { get; } = new("00:00:00:00:00:00");
        public Link? Link { get; set; }

        private readonly Switch _switch;

        internal SwitchPort(int number, Switch sw)
        {
            Number = number;
            Name = $"{sw.Name}:Port{number}";
            _switch = sw;
        }

        public void Receive(EthernetFrame frame) => _switch.ProcessFrame(frame, Number);

        public void Deliver(EthernetFrame frame) => Link?.Deliver(frame, this);
    }

    public string Name { get; }

    private readonly List<SwitchPort> _ports = new();
    private readonly Dictionary<MacAddress, int> _macTable = new();
    private readonly ILogger _logger;

    public IReadOnlyDictionary<MacAddress, int> MacTable => _macTable;

    public Switch(string name, ILogger? logger = null)
    {
        Name = name;
        _logger = logger ?? new ConsoleLogger();
    }

    public Link Connect(INetworkDevice device)
    {
        var port = new SwitchPort(_ports.Count + 1, this);
        var link = new Link(device, port);
        port.Link = link;
        _ports.Add(port);
        _logger.Log($"[{Name}] {device.Name} connected on port {port.Number}");
        return link;
    }

    private void ProcessFrame(EthernetFrame frame, int incomingPortNumber)
    {
        _logger.Log($"[{Name}] Port {incomingPortNumber} received: {frame}");

        if (!_macTable.ContainsKey(frame.Source))
        {
            _macTable[frame.Source] = incomingPortNumber;
            _logger.Log($"[{Name}] Learned: {frame.Source} → port {incomingPortNumber}");
        }

        if (frame.Destination.IsBroadcast || !_macTable.TryGetValue(frame.Destination, out var destPort))
        {
            Flood(frame, excludePort: incomingPortNumber);
        }
        else
        {
            _logger.Log($"[{Name}] Forwarding {frame.Destination} → port {destPort}");
            _ports[destPort - 1].Deliver(frame);
        }
    }

    private void Flood(EthernetFrame frame, int excludePort)
    {
        foreach (var port in _ports.Where(p => p.Number != excludePort))
        {
            _logger.Log($"[{Name}] Flooding → port {port.Number} ({port.Name})");
            port.Deliver(frame);
        }
    }
}
