namespace MiniNet.Server.Models;

public sealed class HostRegistrationDto
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public int PrefixLength { get; set; }
    public string Gateway { get; set; } = "";
    public string Switch { get; set; } = "";
}
