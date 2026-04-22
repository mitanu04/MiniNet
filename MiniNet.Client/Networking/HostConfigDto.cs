namespace MiniNet.Client.Networking;

internal sealed class HostConfigDto
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public int PrefixLength { get; set; }
    public string Gateway { get; set; } = "";
    public string Switch { get; set; } = "";
}
