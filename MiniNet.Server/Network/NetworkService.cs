using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MiniNet.Server.Data;
using MiniNet.Server.Hubs;
using MiniNet.Server.Models;
using MiniNet.Server.Network.Config;

namespace MiniNet.Server.Network;

public sealed class NetworkService : IFrameFabric
{
    private sealed record HostInfo(string ConnectionId, string Name, string Mac, string Ip, string Gateway, string SwitchName);

    private readonly IHubContext<NetworkHub> _hub;
    private readonly NetworkTopologyConfig _config;
    private readonly IDbContextFactory<NetworkDbContext> _dbFactory;

    private readonly ConcurrentDictionary<string, HostInfo> _hosts = new();
    private readonly ConcurrentDictionary<string, SimulatedHost> _simulatedHosts = new();

    // switchName → (mac → FrameDestination)
    private readonly Dictionary<string, Dictionary<string, FrameDestination>> _macTables = new();

    private readonly Dictionary<string, RouterLogic> _routers = new();

    // switchName → (baseIp, prefixLength, gateway)
    private readonly Dictionary<string, (uint BaseIp, int PrefixLength, string Gateway)> _switchSubnets = new();
    private readonly Dictionary<string, int> _nextHostIndex = new();   // next host octet per switch
    private int _dynamicSubnetIndex = 0;                               // for switches with no router

    private readonly object _routingLock = new();

    public NetworkService(IHubContext<NetworkHub> hub, NetworkTopologyConfig config,
        IDbContextFactory<NetworkDbContext> dbFactory)
    {
        _hub = hub;
        _config = config;
        _dbFactory = dbFactory;
        Initialize();
    }

    private void Initialize()
    {
        foreach (var sw in _config.Switches)
            _macTables[sw.Name] = new Dictionary<string, FrameDestination>();

        foreach (var r in _config.Routers)
        {
            var router = new RouterLogic(r);
            _routers[r.Name] = router;

            foreach (var iface in r.Interfaces)
            {
                _macTables[iface.Switch][iface.Mac] = new RouterPortDestination(r.Name, iface.Name);
                // Register subnet for this switch (gateway = router interface IP)
                if (!_switchSubnets.ContainsKey(iface.Switch))
                {
                    var baseIp = IpToUInt32(iface.Ip) & PrefixMask(iface.PrefixLength);
                    _switchSubnets[iface.Switch] = (baseIp, iface.PrefixLength, iface.Ip);
                }
            }
        }
    }

    private void EnsureSwitchSubnet(string switchName, string? preferredRouterName = null)
    {
        if (_switchSubnets.ContainsKey(switchName)) return;

        var baseIp    = IpToUInt32("172.16.0.0") + (uint)(_dynamicSubnetIndex * 256);
        var routerIp  = UInt32ToIp(baseIp + 1);
        var routerMac = $"AA:00:01:00:00:{_dynamicSubnetIndex + 1:X2}";
        _dynamicSubnetIndex++;

        // Pick router: preferred → first available → none
        RouterLogic? router = null;
        if (!string.IsNullOrEmpty(preferredRouterName))
            _routers.TryGetValue(preferredRouterName, out router);
        router ??= _routers.Values.FirstOrDefault();

        _switchSubnets[switchName] = (baseIp, 24, router != null ? routerIp : "");

        if (router != null)
        {
            var routerConfig = _config.Routers.First(r => r.Name == router.Name);
            var ifaceName    = $"eth{routerConfig.Interfaces.Count}";
            var iface = new RouterInterfaceConfig
            {
                Name = ifaceName, Mac = routerMac,
                Ip = routerIp, PrefixLength = 24, Switch = switchName
            };
            router.AddInterface(iface);

            if (_macTables.TryGetValue(switchName, out var table))
                table[routerMac] = new RouterPortDestination(router.Name, ifaceName);
        }
    }

    private (string Ip, int PrefixLength, string Gateway) AutoAssignIp(string switchName)
    {
        EnsureSwitchSubnet(switchName);
        var (baseIp, prefix, gateway) = _switchSubnets[switchName];

        var usedIps = new HashSet<string>(
            _hosts.Values.Select(h => h.Ip)
                .Concat(_simulatedHosts.Values.Select(s => s.Ip))
                .Concat(_config.Routers.SelectMany(r => r.Interfaces).Select(i => i.Ip)));

        var index = _nextHostIndex.GetValueOrDefault(switchName, 2);
        while (index < 254)
        {
            var candidate = UInt32ToIp(baseIp + (uint)index);
            if (!usedIps.Contains(candidate))
            {
                _nextHostIndex[switchName] = index + 1;
                return (candidate, prefix, gateway);
            }
            index++;
        }
        return ("", prefix, gateway); // subnet exhausted
    }

    private static uint IpToUInt32(string ip) =>
        ip.Split('.').Aggregate(0u, (acc, b) => (acc << 8) | byte.Parse(b));

    private static uint PrefixMask(int prefix) =>
        prefix == 0 ? 0u : ~0u << (32 - prefix);

    private static string UInt32ToIp(uint ip) =>
        $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    // ── Persistence ───────────────────────────────────────────────────────────

    public async Task LoadPersistedStateAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Restore routers first (switches need them for auto-connect)
        foreach (var r in await db.Routers.OrderBy(r => r.Id).ToListAsync())
        {
            if (_routers.ContainsKey(r.Name)) continue;
            var routerConfig = new RouterConfig { Name = r.Name };
            _config.Routers.Add(routerConfig);
            _routers[r.Name] = new RouterLogic(routerConfig);
        }

        // Restore switches
        foreach (var sw in await db.Switches.OrderBy(s => s.Id).ToListAsync())
        {
            lock (_routingLock)
            {
                if (_macTables.ContainsKey(sw.Name)) continue;
                _macTables[sw.Name] = new Dictionary<string, FrameDestination>();
                EnsureSwitchSubnet(sw.Name, string.IsNullOrEmpty(sw.RouterName) ? null : sw.RouterName);
            }
        }

        // Restore simulated devices
        foreach (var d in await db.Devices.ToListAsync())
        {
            if (_simulatedHosts.ContainsKey(d.Name)) continue;

            var sim = new SimulatedHost(d.Name, d.Mac, d.Ip, d.PrefixLength, d.Gateway, d.SwitchName);
            _simulatedHosts[d.Name] = sim;

            lock (_routingLock)
            {
                if (_macTables.TryGetValue(d.SwitchName, out var table))
                    table[d.Mac] = new SimulatedHostDestination(d.Name);
                // Mark IP as used so auto-assign skips it
                if (_switchSubnets.TryGetValue(d.SwitchName, out var subnet))
                {
                    var hostOctet = (int)(IpToUInt32(d.Ip) - subnet.BaseIp);
                    if (_nextHostIndex.GetValueOrDefault(d.SwitchName, 2) <= hostOctet)
                        _nextHostIndex[d.SwitchName] = hostOctet + 1;
                }
            }

            foreach (var router in _routers.Values)
                router.LearnHost(d.Ip, d.Mac);
        }
    }

    // ── Dynamic topology ──────────────────────────────────────────────────────

    public async Task CreateSwitch(string name, string? routerName = null)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name)) return;

        lock (_routingLock)
        {
            if (_macTables.ContainsKey(name))
                throw new HubException($"A switch named '{name}' already exists.");
            _macTables[name] = new Dictionary<string, FrameDestination>();
            EnsureSwitchSubnet(name, routerName);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.Switches.AnyAsync(s => s.Name == name))
        {
            db.Switches.Add(new SwitchRecord { Name = name, RouterName = routerName ?? "" });
            await db.SaveChangesAsync();
        }

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "SwitchCreated",
            Description = $"Switch {name} added to topology",
            SrcDevice = name
        });

        await BroadcastTopology();
    }

    public async Task CreateRouter(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name)) return;

        lock (_routingLock)
        {
            if (_routers.ContainsKey(name)) return;
            var routerConfig = new RouterConfig { Name = name };
            _config.Routers.Add(routerConfig);
            _routers[name] = new RouterLogic(routerConfig);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.Routers.AnyAsync(r => r.Name == name))
        {
            db.Routers.Add(new RouterRecord { Name = name });
            await db.SaveChangesAsync();
        }

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "SwitchCreated",
            Description = $"Router {name} added to topology",
            SrcDevice = name
        });
        await BroadcastTopology();
    }

    public async Task CreateDevice(CreateDeviceDto dto)
    {
        var mac = string.IsNullOrWhiteSpace(dto.Mac)
            ? GenerateMac(dto.Name)
            : dto.Mac.Trim();

        string ip;
        int prefixLength;
        string gateway;
        lock (_routingLock)
        {
            (ip, prefixLength, gateway) = string.IsNullOrWhiteSpace(dto.Ip)
                ? AutoAssignIp(dto.Switch)
                : (dto.Ip, dto.PrefixLength, dto.Gateway);
        }

        if (string.IsNullOrEmpty(ip))
        {
            await BroadcastEvent(new NetworkEventDto
            {
                EventType = "PacketDropped",
                Description = $"Cannot add {dto.Name}: subnet {dto.Switch} is full"
            });
            return;
        }

        var sim = new SimulatedHost(dto.Name, mac, ip, prefixLength, gateway, dto.Switch);
        _simulatedHosts[dto.Name] = sim;

        lock (_routingLock)
        {
            if (_macTables.TryGetValue(dto.Switch, out var table))
                table[mac] = new SimulatedHostDestination(dto.Name);
        }

        foreach (var router in _routers.Values)
            router.LearnHost(ip, mac);

        // Gratuitous ARP — announces presence to the segment
        await SendFrameOnSwitch(dto.Switch, new SendFrameDto
        {
            FrameType = "ARP",
            SrcMac = mac,
            DstMac = "FF:FF:FF:FF:FF:FF",
            Arp = new ArpDto
            {
                Operation = "Reply",
                SenderMac = mac,
                SenderIp = ip,
                TargetMac = "FF:FF:FF:FF:FF:FF",
                TargetIp = ip
            }
        });

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.Devices.AnyAsync(d => d.Name == dto.Name))
        {
            db.Devices.Add(new DeviceRecord
            {
                Name = dto.Name, Mac = mac, Ip = ip,
                PrefixLength = prefixLength, Gateway = gateway, SwitchName = dto.Switch
            });
            await db.SaveChangesAsync();
        }

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "DeviceConnected",
            Description = $"{dto.Name} ({ip}/{prefixLength} / {mac}) connected to {dto.Switch} [simulated]",
            SrcDevice = dto.Name
        });

        await BroadcastTopology();
    }

    // ── Real host lifecycle ───────────────────────────────────────────────────

    public List<string> GetSwitches()
    {
        lock (_routingLock)
            return _macTables.Keys.OrderBy(k => k).ToList();
    }

    public async Task<HostConfigDto> RegisterHostAuto(string connectionId, string name, string switchName)
    {
        var mac = GenerateMac(name);

        // Reject if a real host with the same name is already connected
        if (_hosts.Values.Any(h => h.Name == name))
            throw new HubException($"A device named '{name}' is already connected. Choose a different name.");

        // If a simulated host with the same name exists, remove it so the real client takes over
        if (_simulatedHosts.TryRemove(name, out var existingSim))
        {
            lock (_routingLock)
            {
                if (_macTables.TryGetValue(existingSim.SwitchName, out var t))
                    t.Remove(existingSim.Mac);
            }
        }

        string ip; int prefixLength; string gateway;
        lock (_routingLock)
            (ip, prefixLength, gateway) = AutoAssignIp(switchName);

        if (string.IsNullOrEmpty(ip))
            throw new HubException($"Subnet for switch '{switchName}' is full.");

        var host = new HostInfo(connectionId, name, mac, ip, gateway, switchName);
        _hosts[connectionId] = host;

        lock (_routingLock)
        {
            if (_macTables.TryGetValue(switchName, out var table))
                table[mac] = new HostDestination(connectionId);
        }

        foreach (var router in _routers.Values)
            router.LearnHost(ip, mac);

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "DeviceConnected",
            Description = $"{name} ({ip}/{prefixLength} / {mac}) connected to {switchName}",
            SrcDevice = name
        });

        await BroadcastTopology();

        return new HostConfigDto
        {
            Name = name, Mac = mac, Ip = ip,
            PrefixLength = prefixLength, Gateway = gateway, Switch = switchName
        };
    }

    public async Task RegisterHost(string connectionId, HostRegistrationDto dto)
    {
        var host = new HostInfo(connectionId, dto.Name, dto.Mac, dto.Ip, dto.Gateway, dto.Switch);
        _hosts[connectionId] = host;

        lock (_routingLock)
        {
            if (_macTables.TryGetValue(dto.Switch, out var table))
                table[dto.Mac] = new HostDestination(connectionId);
        }

        foreach (var router in _routers.Values)
            router.LearnHost(dto.Ip, dto.Mac);

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "DeviceConnected",
            Description = $"{dto.Name} connected ({dto.Ip}) on {dto.Switch}",
            SrcDevice = dto.Name
        });

        await BroadcastTopology();
    }

    public async Task UnregisterHost(string connectionId)
    {
        if (!_hosts.TryRemove(connectionId, out var host)) return;

        lock (_routingLock)
        {
            if (_macTables.TryGetValue(host.SwitchName, out var table))
                table.Remove(host.Mac);
        }

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "DeviceDisconnected",
            Description = $"{host.Name} disconnected",
            SrcDevice = host.Name
        });

        await BroadcastTopology();
    }

    // ── Frame processing ──────────────────────────────────────────────────────

    public async Task ProcessFrame(string senderConnectionId, SendFrameDto frame)
    {
        if (!_hosts.TryGetValue(senderConnectionId, out var sender)) return;

        string switchName;
        Dictionary<string, FrameDestination> table;

        lock (_routingLock)
        {
            switchName = sender.SwitchName;
            if (!_macTables.TryGetValue(switchName, out table!)) return;
            table[frame.SrcMac] = new HostDestination(senderConnectionId);
        }

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = DetermineEventType(frame),
            Description = BuildDescription(frame, sender.Name),
            SrcDevice = sender.Name,
            DstDevice = frame.DstMac
        });

        if (frame.DstMac == "FF:FF:FF:FF:FF:FF")
        {
            await FloodSwitch(switchName, frame, excludeMac: frame.SrcMac);
            return;
        }

        FrameDestination? dest;
        lock (_routingLock)
            table.TryGetValue(frame.DstMac, out dest);

        if (dest != null) await Dispatch(dest, frame);
        else await FloodSwitch(switchName, frame, excludeMac: frame.SrcMac);
    }

    // IFrameFabric — called by RouterLogic and SimulatedHost
    public async Task SendFrameOnSwitch(string switchName, SendFrameDto frame)
    {
        if (frame.DstMac == "FF:FF:FF:FF:FF:FF")
        {
            await FloodSwitch(switchName, frame, excludeMac: frame.SrcMac);
            return;
        }

        FrameDestination? dest = null;
        lock (_routingLock)
        {
            if (_macTables.TryGetValue(switchName, out var table))
                table.TryGetValue(frame.DstMac, out dest);
        }

        if (dest != null) await Dispatch(dest, frame);
        else await FloodSwitch(switchName, frame, excludeMac: frame.SrcMac);
    }

    public async Task BroadcastEvent(NetworkEventDto evt)
    {
        evt.Timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        await _hub.Clients.Group("dashboard").SendAsync("NetworkEvent", evt);
        PersistEventFireAndForget(evt);
    }

    private static readonly HashSet<string> _persistedEventTypes =
    [
        "SwitchCreated", "DeviceConnected", "DeviceDisconnected",
        "GratuitousArp", "IcmpEchoRequest", "IcmpEchoReply",
        "PacketDropped", "TtlExpired",
        "TcpSyn", "TcpSynAck", "TcpAck", "TcpData", "TcpRetransmit", "TcpFin"
    ];

    private void PersistEventFireAndForget(NetworkEventDto evt)
    {
        if (!_persistedEventTypes.Contains(evt.EventType ?? "")) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                db.Events.Add(new EventRecord
                {
                    Timestamp   = evt.Timestamp ?? "",
                    EventType   = evt.EventType ?? "",
                    Description = evt.Description ?? "",
                    SrcDevice   = evt.SrcDevice ?? "",
                    DstDevice   = evt.DstDevice ?? ""
                });
                await db.SaveChangesAsync();
            }
            catch { /* non-critical */ }
        });
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteDevice(string name)
    {
        if (!_simulatedHosts.TryRemove(name, out var sim)) return;

        lock (_routingLock)
        {
            if (_macTables.TryGetValue(sim.SwitchName, out var table))
                table.Remove(sim.Mac);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var record = await db.Devices.FirstOrDefaultAsync(d => d.Name == name);
                if (record != null) { db.Devices.Remove(record); await db.SaveChangesAsync(); }
            }
            catch { }
        });

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "DeviceDisconnected",
            Description = $"{name} removed from topology",
            SrcDevice = name
        });
        await BroadcastTopology();
    }

    public async Task DeleteRouter(string name)
    {
        lock (_routingLock)
        {
            if (!_routers.ContainsKey(name)) return;

            var routerConfig = _config.Routers.FirstOrDefault(r => r.Name == name);
            if (routerConfig != null)
            {
                // Remove router MAC entries from switch MAC tables and clear gateways
                foreach (var iface in routerConfig.Interfaces)
                {
                    if (_macTables.TryGetValue(iface.Switch, out var table))
                        table.Remove(iface.Mac);
                    if (_switchSubnets.TryGetValue(iface.Switch, out var subnet))
                        _switchSubnets[iface.Switch] = (subnet.BaseIp, subnet.PrefixLength, "");
                }
                _config.Routers.Remove(routerConfig);
            }
            _routers.Remove(name);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var record = await db.Routers.FirstOrDefaultAsync(r => r.Name == name);
                if (record != null) { db.Routers.Remove(record); await db.SaveChangesAsync(); }
            }
            catch { }
        });

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "SwitchCreated",
            Description = $"Router {name} removed from topology",
            SrcDevice = name
        });
        await BroadcastTopology();
    }

    public async Task DeleteSwitch(string name)
    {
        List<string> devicesToRemove;
        lock (_routingLock)
        {
            if (!_macTables.ContainsKey(name)) return;

            // Remove router interfaces attached to this switch
            foreach (var routerConfig in _config.Routers)
                routerConfig.Interfaces.RemoveAll(i => i.Switch == name);

            devicesToRemove = _simulatedHosts.Values
                .Where(s => s.SwitchName == name)
                .Select(s => s.Name).ToList();
            _macTables.Remove(name);
            _switchSubnets.Remove(name);
        }

        foreach (var dev in devicesToRemove)
            _simulatedHosts.TryRemove(dev, out _);

        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var sw = await db.Switches.FirstOrDefaultAsync(s => s.Name == name);
                if (sw != null) db.Switches.Remove(sw);
                var devices = db.Devices.Where(d => d.SwitchName == name);
                db.Devices.RemoveRange(devices);
                await db.SaveChangesAsync();
            }
            catch { }
        });

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "SwitchCreated",
            Description = $"Switch {name} removed from topology",
            SrcDevice = name
        });
        await BroadcastTopology();
    }

    public TopologyDto GetTopology()
    {
        var topology = new TopologyDto();

        List<string> allSwitches;
        lock (_routingLock)
            allSwitches = _macTables.Keys.OrderBy(k => k).ToList();

        foreach (var swName in allSwitches)
            topology.Nodes.Add(new NodeDto { Id = swName, Label = swName, Type = "switch" });

        foreach (var r in _config.Routers)
        {
            var ifaceInfo = string.Join("\n", r.Interfaces.Select(i => $"{i.Name}: {i.Ip} ({i.Mac})"));
            topology.Nodes.Add(new NodeDto
            {
                Id = r.Name, Label = r.Name, Type = "router",
                Tooltip = $"{r.Name}\n{ifaceInfo}"
            });
            foreach (var iface in r.Interfaces)
                topology.Edges.Add(new EdgeDto
                {
                    From = r.Name, To = iface.Switch,
                    Label = $"{iface.Name}\n{iface.Ip}/{iface.PrefixLength}\n{iface.Mac}"
                });
        }

        foreach (var host in _hosts.Values)
        {
            topology.Nodes.Add(new NodeDto
            {
                Id = host.Name,
                Label = $"{host.Name}\n{host.Ip}\n{host.Mac}",
                Type = "host",
                Tooltip = $"{host.Name}\nIP:  {host.Ip}\nMAC: {host.Mac}\nGW:  {host.Gateway}\nSW:  {host.SwitchName}"
            });
            topology.Edges.Add(new EdgeDto { From = host.Name, To = host.SwitchName });
        }

        foreach (var sim in _simulatedHosts.Values)
        {
            topology.Nodes.Add(new NodeDto
            {
                Id = sim.Name,
                Label = $"{sim.Name}\n{sim.Ip}\n{sim.Mac}",
                Type = "simhost",
                Tooltip = $"{sim.Name} [simulated]\nIP:  {sim.Ip}\nMAC: {sim.Mac}\nGW:  {sim.Gateway}\nSW:  {sim.SwitchName}"
            });
            topology.Edges.Add(new EdgeDto { From = sim.Name, To = sim.SwitchName });
        }

        return topology;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task BroadcastTopology() =>
        await _hub.Clients.Group("dashboard").SendAsync("TopologyUpdate", GetTopology());

    private async Task Dispatch(FrameDestination dest, SendFrameDto frame)
    {
        switch (dest)
        {
            case HostDestination hd:
                await _hub.Clients.Client(hd.ConnectionId).SendAsync("ReceiveFrame", frame);
                break;
            case RouterPortDestination rpd:
                if (_routers.TryGetValue(rpd.RouterName, out var router))
                    await router.ReceiveFrame(frame, rpd.InterfaceName, this);
                break;
            case SimulatedHostDestination shd:
                if (_simulatedHosts.TryGetValue(shd.HostName, out var sim))
                    await sim.HandleFrame(frame, this);
                break;
        }
    }

    private async Task FloodSwitch(string switchName, SendFrameDto frame, string excludeMac)
    {
        List<FrameDestination> targets;
        lock (_routingLock)
        {
            if (!_macTables.TryGetValue(switchName, out var table)) return;
            targets = table.Where(kv => kv.Key != excludeMac).Select(kv => kv.Value).ToList();
        }

        await BroadcastEvent(new NetworkEventDto
        {
            EventType = "FrameFlooded",
            Description = $"{switchName}: flooding {targets.Count} port(s) (src {excludeMac})"
        });

        await Task.WhenAll(targets.Select(d => Dispatch(d, frame)));
    }

    private static string GenerateMac(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        hash[0] = (byte)((hash[0] & 0xFE) | 0x02); // locally administered, unicast
        return string.Join(":", hash.Take(6).Select(b => b.ToString("X2")));
    }

    private static bool IsGratuitousArp(SendFrameDto frame) =>
        frame.FrameType == "ARP" && frame.Arp != null && frame.Arp.SenderIp == frame.Arp.TargetIp;

    private static string DetermineEventType(SendFrameDto frame) => frame.FrameType switch
    {
        "ARP" when IsGratuitousArp(frame)                    => "GratuitousArp",
        "ARP" when frame.Arp?.Operation == "Request"         => "ArpRequest",
        "ARP"                                                 => "ArpReply",
        "IPv4" when frame.Ip?.Icmp?.Type == "EchoRequest"    => "IcmpEchoRequest",
        "IPv4" when frame.Ip?.Icmp?.Type == "EchoReply"      => "IcmpEchoReply",
        _                                                     => "FrameForwarded"
    };

    private static string BuildDescription(SendFrameDto frame, string senderName) => frame.FrameType switch
    {
        "ARP" when IsGratuitousArp(frame) =>
            $"{senderName}: Gratuitous ARP — {frame.Arp!.SenderIp} is at {frame.Arp.SenderMac}",
        "ARP" when frame.Arp?.Operation == "Request" =>
            $"{senderName}: ARP request — who has {frame.Arp.TargetIp}?",
        "ARP" =>
            $"{senderName}: ARP reply — {frame.Arp!.SenderIp} is at {frame.Arp.SenderMac}",
        "IPv4" when frame.Ip?.Icmp?.Type == "EchoRequest" =>
            $"{senderName}: ping {frame.Ip.SrcIp} → {frame.Ip.DstIp} seq={frame.Ip.Icmp.SequenceNumber}",
        "IPv4" when frame.Ip?.Icmp?.Type == "EchoReply" =>
            $"{senderName}: pong {frame.Ip.SrcIp} → {frame.Ip.DstIp} seq={frame.Ip.Icmp.SequenceNumber}",
        _ =>
            $"{senderName}: {frame.FrameType} {frame.SrcMac} → {frame.DstMac}"
    };
}
