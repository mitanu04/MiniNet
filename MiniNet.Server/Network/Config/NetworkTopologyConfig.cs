namespace MiniNet.Server.Network.Config;

public sealed class NetworkTopologyConfig
{
    public List<SwitchConfig> Switches { get; set; } = new();
    public List<RouterConfig> Routers { get; set; } = new();
}

public sealed class SwitchConfig
{
    public string Name { get; set; } = "";
}

public sealed class RouterConfig
{
    public string Name { get; set; } = "";
    public List<RouterInterfaceConfig> Interfaces { get; set; } = new();
    public List<StaticRouteConfig> Routes { get; set; } = new();
}

public sealed class RouterInterfaceConfig
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public int PrefixLength { get; set; }
    public string Switch { get; set; } = "";
}

public sealed class StaticRouteConfig
{
    public string Destination { get; set; } = "";
    public int PrefixLength { get; set; }
    public string Via { get; set; } = "";
}
