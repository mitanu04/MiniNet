using MiniNet.Server.Models;

namespace MiniNet.Server.Network;

public interface IFrameFabric
{
    Task SendFrameOnSwitch(string switchName, SendFrameDto frame);
    Task BroadcastEvent(NetworkEventDto evt);
}
