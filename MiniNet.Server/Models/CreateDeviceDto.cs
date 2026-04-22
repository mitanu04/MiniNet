namespace MiniNet.Server.Models;

public sealed class CreateDeviceDto
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public string Gateway { get; set; } = "";
    public string Switch { get; set; } = "";
    // MAC is auto-generated from Name if omitted
    public string? Mac { get; set; }
}
