using MiniNet.Core.Devices;

var hostA = new Host("HostA", "AA:AA:AA:AA:AA:AA", "10.0.0.1");
var hostB = new Host("HostB", "BB:BB:BB:BB:BB:BB", "10.0.0.2");
var hostC = new Host("HostC", "CC:CC:CC:CC:CC:CC", "10.0.0.3");

var sw = new Switch("Switch1");
hostA.Link = sw.Connect(hostA);
hostB.Link = sw.Connect(hostB);
hostC.Link = sw.Connect(hostC);

Console.WriteLine("\n--- HostA sends IP to HostC (ARP required) ---");
hostA.SendIp("10.0.0.3", "Hello HostC!");

Console.WriteLine("\n--- HostA sends again to HostC (MAC already in ARP table) ---");
hostA.SendIp("10.0.0.3", "Are you there?");

Console.WriteLine("\n--- HostB broadcasts ---");
hostB.SendRaw("FF:FF:FF:FF:FF:FF", "Hello everyone!");
