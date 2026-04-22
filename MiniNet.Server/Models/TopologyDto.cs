namespace MiniNet.Server.Models;

public sealed class TopologyDto
{
    public List<NodeDto> Nodes { get; set; } = new();
    public List<EdgeDto> Edges { get; set; } = new();
}

public sealed class NodeDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = ""; // "switch" | "router" | "host"
    public string? Tooltip { get; set; }
}

public sealed class EdgeDto
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Label { get; set; }
}
