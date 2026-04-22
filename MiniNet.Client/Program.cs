using MiniNet.Client.Cli;
using MiniNet.Client.Networking;

var serverUrl = args.FirstOrDefault(a => a.StartsWith("http")) ?? "http://localhost:5000";

await using var host = new NetworkHost(serverUrl);

Console.WriteLine($"Connecting to {serverUrl}…");
await host.StartAsync();
Console.WriteLine("Connected.");

while (true)
{
    var switches = await host.GetSwitchesAsync();

    var (name, switchName) = await SetupWizard.RunAsync(
        switches,
        async sw =>
        {
            await host.CreateSwitchAsync(sw);
            await Task.Delay(300); // allow server to broadcast topology
        }
    );

    try
    {
        await host.RegisterAutoAsync(name, switchName);
        break;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  Error: {ex.Message}\n");
        Console.ResetColor();
    }
}

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"╔══════════════════════════════════╗");
Console.WriteLine($"║  {host.Name,-32}║");
Console.WriteLine($"║  IP:      {host.Ip}/{host.PrefixLength,-20}║");
Console.WriteLine($"║  MAC:     {host.Mac,-23}║");
Console.WriteLine($"║  Gateway: {host.Gateway,-23}║");
Console.WriteLine($"║  Switch:  {host.Switch,-23}║");
Console.WriteLine($"╚══════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine("Commands: ping <ip> | traceroute <ip> | tcp <ip> <port> <data> | arp | exit\n");

while (true)
{
    Console.Write($"{host.Name}> ");
    var line = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(line)) continue;
    if (line is "exit" or "quit") break;

    if (line.StartsWith("ping "))
    {
        var destIp = line["ping ".Length..].Trim();
        if (string.IsNullOrEmpty(destIp)) { Console.WriteLine("Usage: ping <ip>"); continue; }

        Console.WriteLine($"PING {destIp} — 4 packets");
        for (var i = 1; i <= 4; i++)
        {
            var rtt = await host.PingAsync(destIp);
            Console.WriteLine(rtt.HasValue
                ? $"  Reply from {destIp}: seq={i} time={rtt}ms"
                : $"  Request timeout for seq={i}");
            if (i < 4) await Task.Delay(500);
        }
    }
    else if (line.StartsWith("traceroute "))
    {
        var destIp = line["traceroute ".Length..].Trim();
        if (string.IsNullOrEmpty(destIp)) { Console.WriteLine("Usage: traceroute <ip>"); continue; }
        await host.TracerouteAsync(destIp);
    }
    else if (line == "arp")
    {
        host.PrintArpTable();
    }
    else if (line.StartsWith("tcp "))
    {
        // tcp <ip> <port> <chunk1> <chunk2> ...
        // e.g.: tcp 10.0.0.2 80 Hello World "Goodbye"
        var parts = line["tcp ".Length..].Trim().Split(' ', 3);
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: tcp <ip> <port> <data>");
            continue;
        }
        if (!int.TryParse(parts[1], out var port))
        {
            Console.WriteLine("Invalid port.");
            continue;
        }
        var chunks = parts[2].Split('|')           // split on | for multiple chunks
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .ToArray();
        if (chunks.Length == 0) chunks = [parts[2]];
        await host.TcpSendAsync(parts[0], port, chunks);
    }
    else
    {
        Console.WriteLine("Commands: ping <ip> | traceroute <ip> | tcp <ip> <port> <data> | arp | exit");
    }
}
