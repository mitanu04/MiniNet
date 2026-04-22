using MiniNet.Core.Protocols;

namespace MiniNet.Network;

public sealed class Link
{
    private readonly INetworkDevice _endpointA;
    private readonly INetworkDevice _endpointB;

    public Link(INetworkDevice a, INetworkDevice b)
    {
        _endpointA = a;
        _endpointB = b;
    }

    public void Deliver(EthernetFrame frame, INetworkDevice sender)
    {
        var receiver = ReferenceEquals(sender, _endpointA) ? _endpointB : _endpointA;
        receiver.Receive(frame);
    }
}
