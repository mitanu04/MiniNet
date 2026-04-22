using MiniNet.Core;
using MiniNet.Core.Protocols;

namespace MiniNet.Network;

public interface INetworkDevice
{
    string Name { get; }
    MacAddress MacAddress { get; }
    void Receive(EthernetFrame frame);
}
