using Microsoft.AspNetCore.SignalR;
using MiniNet.Server.Models;
using MiniNet.Server.Network;

namespace MiniNet.Server.Hubs;

public sealed class NetworkHub : Hub
{
    private readonly NetworkService _network;

    public NetworkHub(NetworkService network) => _network = network;

    public List<string> GetSwitches() => _network.GetSwitches();

    public async Task<HostConfigDto> RegisterHostAuto(string name, string switchName) =>
        await _network.RegisterHostAuto(Context.ConnectionId, name, switchName);

    public async Task RegisterHost(HostRegistrationDto dto) =>
        await _network.RegisterHost(Context.ConnectionId, dto);

    public async Task SendFrame(SendFrameDto frame) =>
        await _network.ProcessFrame(Context.ConnectionId, frame);

    public async Task JoinDashboard()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
        var topology = _network.GetTopology();
        await Clients.Caller.SendAsync("TopologyUpdate", topology);
    }

    public async Task CreateRouter(string name) =>
        await _network.CreateRouter(name);

    public async Task CreateSwitch(string name, string? routerName = null) =>
        await _network.CreateSwitch(name, routerName);

    public async Task CreateDevice(CreateDeviceDto dto) =>
        await _network.CreateDevice(dto);

    public async Task DeleteDevice(string name) =>
        await _network.DeleteDevice(name);

    public async Task DeleteRouter(string name) =>
        await _network.DeleteRouter(name);

    public async Task DeleteSwitch(string name) =>
        await _network.DeleteSwitch(name);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _network.UnregisterHost(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
