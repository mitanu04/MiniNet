using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace MiniNet.Client.Networking;

public sealed class NetworkHost : IAsyncDisposable
{
    public string Name    { get; private set; } = "";
    public string Mac     { get; private set; } = "";
    public string Ip      { get; private set; } = "";
    public int PrefixLength { get; private set; }
    public string Gateway { get; private set; } = "";
    public string Switch  { get; private set; } = "";

    private readonly HubConnection _hub;
    private readonly Dictionary<string, string> _arpTable = new();
    private readonly ConcurrentDictionary<(int id, int seq), TaskCompletionSource<long>> _pendingPings = new();
    private int _pingId;

    // Traceroute state: (id, seq) → TCS resolving (hopIp, destinationReached)
    private readonly ConcurrentDictionary<(int id, int seq), TaskCompletionSource<(string hopIp, bool reached)>> _pendingTraceroute = new();

    // TCP state: (dstIp, dstPort, srcPort) → TCS waiting for SYN-ACK
    private readonly ConcurrentDictionary<(string, int, int), TaskCompletionSource<int>> _pendingSynAck = new();
    // Active sessions: (dstIp, dstPort, srcPort) → next expected ACK
    private readonly ConcurrentDictionary<(string, int, int), TaskCompletionSource<int>> _pendingAck = new();
    private readonly Random _rng = new();

    private static readonly uint[] _subnetMaskCache = BuildMaskCache();

    public NetworkHost(string serverUrl)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(serverUrl + "/networkHub")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<JsonElement>("ReceiveFrame", frame => HandleFrame(frame));

        _hub.Reconnected += async connectionId =>
        {
            if (_hub.State != HubConnectionState.Connected) return;
            Console.WriteLine($"[{Name}] Reconnected — re-registering…");
            _arpTable.Clear();
            await RegisterAsync();
            await SendGratuitousArpAsync();
        };

        _hub.Closed += error =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{Name}] Connection closed: {error?.Message ?? "unknown reason"}");
            Console.ResetColor();
            return Task.CompletedTask;
        };
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>Start the hub connection without registering. Call RegisterAutoAsync next.</summary>
    public async Task StartAsync()
    {
        if (_hub.State == HubConnectionState.Connected) return;
        await _hub.StartAsync();
    }

    /// <summary>Ask the server to assign IP/MAC automatically and register this host.</summary>
    public async Task RegisterAutoAsync(string name, string switchName)
    {
        var config = await _hub.InvokeAsync<HostConfigDto>("RegisterHostAuto", name, switchName);
        Name = config.Name; Mac = config.Mac; Ip = config.Ip;
        PrefixLength = config.PrefixLength; Gateway = config.Gateway; Switch = config.Switch;
        await SendGratuitousArpAsync();
    }

    /// <summary>Fetch available switch names from the server.</summary>
    public Task<List<string>> GetSwitchesAsync() =>
        _hub.InvokeAsync<List<string>>("GetSwitches");

    /// <summary>Ask the server to create a new switch.</summary>
    public Task CreateSwitchAsync(string name) =>
        _hub.InvokeAsync("CreateSwitch", name);

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public void PrintArpTable()
    {
        if (_arpTable.Count == 0) { Console.WriteLine("  ARP table empty."); return; }
        Console.WriteLine($"  {"IP",-18} {"MAC",-20}");
        Console.WriteLine($"  {new string('-', 38)}");
        foreach (var (ip, mac) in _arpTable)
            Console.WriteLine($"  {ip,-18} {mac,-20}");
    }

    // ── Gratuitous ARP ────────────────────────────────────────────────────────

    private Task SendGratuitousArpAsync()
    {
        Console.WriteLine($"[{Name}] Sending Gratuitous ARP — {Ip} is at {Mac}");
        return _hub.InvokeAsync("SendFrame", new
        {
            frameType = "ARP", srcMac = Mac, dstMac = "FF:FF:FF:FF:FF:FF",
            arp = new { operation = "Reply", senderMac = Mac, senderIp = Ip,
                        targetMac = "FF:FF:FF:FF:FF:FF", targetIp = Ip }
        });
    }

    private Task RegisterAsync() => _hub.InvokeAsync("RegisterHostAuto", Name, Switch);

    // ── Ping ─────────────────────────────────────────────────────────────────

    public async Task<long?> PingAsync(string destIp, int timeoutMs = 3000)
    {
        var destMac = await ResolveArpAsync(destIp);
        if (destMac == null) { Console.WriteLine($"[{Name}] ARP failed for {destIp}"); return null; }

        var id  = Interlocked.Increment(ref _pingId) & 0xFFFF;
        var seq = 1;
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPings[(id, seq)] = tcs;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await _hub.InvokeAsync("SendFrame", new
        {
            frameType = "IPv4", srcMac = Mac, dstMac = destMac,
            ip = new { srcIp = Ip, dstIp = destIp, protocol = "ICMP", ttl = 64,
                       icmp = new { type = "EchoRequest", identifier = id, sequenceNumber = seq, payload = "MiniNet" } }
        });

        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetCanceled());

        try   { await tcs.Task; return stopwatch.ElapsedMilliseconds; }
        catch (TaskCanceledException) { _pendingPings.TryRemove((id, seq), out _); return null; }
    }

    // ── Traceroute ────────────────────────────────────────────────────────────

    public async Task TracerouteAsync(string destIp, int maxHops = 15, int timeoutMs = 2000)
    {
        var destMac = await ResolveArpAsync(destIp);
        if (destMac == null) { Console.WriteLine($"[{Name}] ARP failed for {destIp}"); return; }

        Console.WriteLine($"traceroute to {destIp}, max {maxHops} hops");

        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            var id  = Interlocked.Increment(ref _pingId) & 0xFFFF;
            var seq = ttl;
            var tcs = new TaskCompletionSource<(string, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTraceroute[(id, seq)] = tcs;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await _hub.InvokeAsync("SendFrame", new
            {
                frameType = "IPv4", srcMac = Mac, dstMac = destMac,
                ip = new { srcIp = Ip, dstIp = destIp, protocol = "ICMP", ttl,
                           icmp = new { type = "EchoRequest", identifier = id, sequenceNumber = seq, payload = "traceroute" } }
            });

            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                cts.Token.Register(() => tcs.TrySetCanceled());
                var (hopIp, reached) = await tcs.Task;
                stopwatch.Stop();
                Console.WriteLine($"  {ttl,2}  {hopIp,-18}  {stopwatch.ElapsedMilliseconds} ms");
                if (reached) { Console.WriteLine("  Destination reached."); return; }
            }
            catch (TaskCanceledException)
            {
                _pendingTraceroute.TryRemove((id, seq), out _);
                Console.WriteLine($"  {ttl,2}  * * * (timeout)");
            }
        }
        Console.WriteLine("  Max hops reached.");
    }

    // ── TCP ──────────────────────────────────────────────────────────────────

    public async Task TcpSendAsync(string destIp, int destPort, string[] chunks,
        int timeoutMs = 3000, int maxRetries = 3)
    {
        var destMac = await ResolveArpAsync(destIp);
        if (destMac == null) { Console.WriteLine($"[{Name}] ARP failed for {destIp}"); return; }

        var srcPort = _rng.Next(1024, 65535);
        var seq     = _rng.Next(1000, 9999);
        var key     = (destIp, destPort, srcPort);

        // ── Step 1: SYN ──────────────────────────────────────────────────────
        Console.WriteLine($"[{Name}] TCP → {destIp}:{destPort} SYN (seq={seq})");
        var synAckTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSynAck[key] = synAckTcs;

        await SendTcpFrame(destIp, destMac, srcPort, destPort,
            syn: true, seq: seq, ackNum: 0);

        int serverSeq;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => synAckTcs.TrySetCanceled());
            serverSeq = await synAckTcs.Task;
        }
        catch { Console.WriteLine($"[{Name}] TCP SYN timeout — no response from {destIp}:{destPort}"); return; }

        // ── Step 3: ACK (handshake complete) ─────────────────────────────────
        seq++;
        Console.WriteLine($"[{Name}] TCP → {destIp}:{destPort} ACK — ESTABLISHED");
        await SendTcpFrame(destIp, destMac, srcPort, destPort,
            ack: true, seq: seq, ackNum: serverSeq + 1);

        // ── Data transfer ─────────────────────────────────────────────────────
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk    = chunks[i];
            var chunkSeq = seq;
            var sent     = false;

            for (var attempt = 1; attempt <= maxRetries && !sent; attempt++)
            {
                if (attempt > 1)
                    Console.WriteLine($"[{Name}] TCP RETRANSMIT DATA #{i + 1} (attempt {attempt})");
                else
                    Console.WriteLine($"[{Name}] TCP → DATA #{i + 1} [{chunkSeq}-{chunkSeq + chunk.Length - 1}] \"{chunk}\"");

                var ackTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingAck[key] = ackTcs;

                await SendTcpFrame(destIp, destMac, srcPort, destPort,
                    ack: true, seq: chunkSeq, ackNum: serverSeq + 1,
                    payload: chunk, payloadSize: chunk.Length);

                try
                {
                    using var cts = new CancellationTokenSource(timeoutMs);
                    cts.Token.Register(() => ackTcs.TrySetCanceled());
                    await ackTcs.Task;
                    seq += chunk.Length;
                    sent = true;
                }
                catch
                {
                    Console.WriteLine($"[{Name}] TCP TIMEOUT for DATA #{i + 1}");
                }
            }

            if (!sent) { Console.WriteLine($"[{Name}] TCP gave up after {maxRetries} retries"); break; }
        }

        // ── FIN ───────────────────────────────────────────────────────────────
        Console.WriteLine($"[{Name}] TCP → {destIp}:{destPort} FIN");
        await SendTcpFrame(destIp, destMac, srcPort, destPort,
            fin: true, ack: true, seq: seq, ackNum: serverSeq + 1);
    }

    private Task SendTcpFrame(string dstIp, string dstMac, int srcPort, int dstPort,
        bool syn = false, bool ack = false, bool fin = false,
        int seq = 0, int ackNum = 0, string? payload = null, int payloadSize = 0)
    {
        return _hub.InvokeAsync("SendFrame", new
        {
            frameType = "IPv4", srcMac = Mac, dstMac,
            ip = new
            {
                srcIp = Ip, dstIp = dstIp, protocol = "TCP", ttl = 64,
                tcp = new { srcPort, dstPort, syn, ack, fin,
                            sequenceNumber = seq, ackNumber = ackNum,
                            payload, payloadSize }
            }
        });
    }

    // ── ARP ──────────────────────────────────────────────────────────────────

    private async Task<string?> ResolveArpAsync(string targetIp)
    {
        var target = IsInSubnet(targetIp, Ip, PrefixLength) ? targetIp : Gateway;
        if (_arpTable.TryGetValue(target, out var mac)) return mac;

        Console.WriteLine($"[{Name}] ARP request: who has {target}?");
        await _hub.InvokeAsync("SendFrame", new
        {
            frameType = "ARP", srcMac = Mac, dstMac = "FF:FF:FF:FF:FF:FF",
            arp = new { operation = "Request", senderMac = Mac, senderIp = Ip,
                        targetMac = "00:00:00:00:00:00", targetIp = target }
        });

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            if (_arpTable.TryGetValue(target, out mac)) return mac;
        }
        return null;
    }

    // ── Frame handling ────────────────────────────────────────────────────────

    private void HandleFrame(JsonElement frame)
    {
        var frameType = frame.GetStringProp("frameType");
        if (frameType == "ARP")  { HandleArp(frame); return; }
        if (frameType == "IPv4")   HandleIp(frame);
    }

    private void HandleArp(JsonElement frame)
    {
        var arp       = frame.GetProperty("arp");
        var operation = arp.GetStringProp("operation");
        var senderIp  = arp.GetStringProp("senderIp");
        var senderMac = arp.GetStringProp("senderMac");
        var targetIp  = arp.GetStringProp("targetIp");

        _arpTable[senderIp] = senderMac;

        if (operation == "Request" && targetIp == Ip)
        {
            _ = _hub.InvokeAsync("SendFrame", new
            {
                frameType = "ARP", srcMac = Mac, dstMac = senderMac,
                arp = new { operation = "Reply", senderMac = Mac, senderIp = Ip,
                            targetMac = senderMac, targetIp = senderIp }
            });
        }
        else if (operation == "Reply")
        {
            Console.WriteLine($"[{Name}] ARP reply: {senderIp} is at {senderMac}");
        }
    }

    private void HandleIp(JsonElement frame)
    {
        if (!frame.TryGetProperty("ip", out var ip)) return;

        if (ip.TryGetProperty("icmp", out var icmp))
        {
            var icmpType = icmp.GetStringProp("type");
            var srcIp    = ip.GetStringProp("srcIp");
            var id       = icmp.GetIntProp("identifier");
            var seq      = icmp.GetIntProp("sequenceNumber");

            if (icmpType == "EchoRequest")
                _ = SendIcmpReply(srcIp, frame.GetStringProp("srcMac"), id, seq);
            else if (icmpType == "EchoReply")
            {
                if (_pendingPings.TryRemove((id, seq), out var tcs)) tcs.TrySetResult(0);
                if (_pendingTraceroute.TryRemove((id, seq), out var trTcs)) trTcs.TrySetResult((srcIp, true));
            }
            else if (icmpType == "TimeExceeded")
            {
                if (_pendingTraceroute.TryRemove((id, seq), out var trTcs)) trTcs.TrySetResult((srcIp, false));
            }
        }

        if (ip.TryGetProperty("tcp", out var tcp))
            HandleTcpFrame(ip, tcp);
    }

    private void HandleTcpFrame(JsonElement ip, JsonElement tcp)
    {
        var srcIp   = ip.GetStringProp("srcIp");
        var srcPort = tcp.GetIntProp("srcPort");
        var dstPort = tcp.GetIntProp("dstPort");
        var syn     = tcp.TryGetProperty("syn", out var s) && s.GetBoolean();
        var ack     = tcp.TryGetProperty("ack", out var a) && a.GetBoolean();
        var fin     = tcp.TryGetProperty("fin", out var f) && f.GetBoolean();
        var seqNum  = tcp.GetIntProp("sequenceNumber");
        var ackNum  = tcp.GetIntProp("ackNumber");

        // SYN-ACK → complete handshake
        if (syn && ack)
        {
            var key = (srcIp, srcPort, dstPort);
            Console.WriteLine($"[{Name}] TCP ← SYN-ACK from {srcIp}:{srcPort} (seq={seqNum}, ack={ackNum})");
            if (_pendingSynAck.TryRemove(key, out var tcs))
                tcs.TrySetResult(seqNum);
            return;
        }

        // ACK for data
        if (ack && !syn && !fin)
        {
            var key = (srcIp, srcPort, dstPort);
            Console.WriteLine($"[{Name}] TCP ← ACK from {srcIp}:{srcPort} (ack={ackNum})");
            if (_pendingAck.TryRemove(key, out var tcs))
                tcs.TrySetResult(ackNum);
            return;
        }

        // FIN-ACK
        if (fin && ack)
            Console.WriteLine($"[{Name}] TCP ← FIN-ACK from {srcIp}:{srcPort} — connection CLOSED");
    }

    private async Task SendIcmpReply(string destIp, string senderMac, int id, int seq)
    {
        var destMac = await ResolveArpAsync(destIp);
        if (destMac == null) return;

        await _hub.InvokeAsync("SendFrame", new
        {
            frameType = "IPv4", srcMac = Mac, dstMac = destMac,
            ip = new { srcIp = Ip, dstIp = destIp, protocol = "ICMP", ttl = 64,
                       icmp = new { type = "EchoReply", identifier = id, sequenceNumber = seq, payload = "MiniNet" } }
        });
    }

    // ── Subnet math ───────────────────────────────────────────────────────────

    private static bool IsInSubnet(string ip, string network, int prefix)
    {
        uint mask = _subnetMaskCache[prefix];
        return (ToUInt32(ip) & mask) == (ToUInt32(network) & mask);
    }

    private static uint ToUInt32(string ip)
    {
        var p = ip.Split('.');
        return (uint.Parse(p[0]) << 24) | (uint.Parse(p[1]) << 16) |
               (uint.Parse(p[2]) << 8)  |  uint.Parse(p[3]);
    }

    private static uint[] BuildMaskCache()
    {
        var cache = new uint[33];
        for (var i = 0; i <= 32; i++)
            cache[i] = i == 0 ? 0u : ~0u << (32 - i);
        return cache;
    }

    public ValueTask DisposeAsync() => _hub.DisposeAsync();
}
