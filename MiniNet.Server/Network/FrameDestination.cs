namespace MiniNet.Server.Network;

public abstract record FrameDestination;
public sealed record HostDestination(string ConnectionId) : FrameDestination;
public sealed record RouterPortDestination(string RouterName, string InterfaceName) : FrameDestination;
public sealed record SimulatedHostDestination(string HostName) : FrameDestination;
