# MiniNet

MiniNet este o simulare educațională a unei rețele de calculatoare, implementată în .NET 8.0. Proiectul demonstrează principiile fundamentale ale rețelelor, inclusiv protocoalele OSI Layer 2 (Ethernet/ARP), Layer 3 (IPv4/ICMP) și Layer 4 (TCP), cu suport pentru conexiuni real-time client-server.

## Structura proiectului

```
MiniNet.sln
├── MiniNet/           # Biblioteca core (protocoale și dispozitive)
├── MiniNet.Server/    # Server ASP.NET Core cu SignalR
├── MiniNet.Client/    # Client CLI
└── MiniNet.Tests/     # Teste unitare (xUnit)
```

---

## MiniNet (Core)

Biblioteca de bază conține implementările protocoalelor și dispozitivelor de rețea.

### Tipuri fundamentale

| Tip | Descriere |
|-----|-----------|
| `MacAddress` | Adresă MAC 48-bit cu validare format `AA:BB:CC:DD:EE:FF` |
| `IpAddress` | Adresă IPv4 cu validare și comparare |
| `EtherType` | Enum: `IPv4 (0x0800)`, `ARP (0x0806)`, `IPv6 (0x86DD)` |

### Protocoale

- **`EthernetFrame`** — Frame Layer 2 cu MAC sursă/destinație, EtherType și payload
- **`ArpPacket`** — Address Resolution Protocol (Request / Reply), rezolvă IP → MAC

### Dispozitive

- **`Host`** — Emulator de calculator cu IP, MAC, tabel ARP intern; trimite/primește frame-uri, răspunde la ARP
- **`Switch`** — Switch Layer 2 cu MAC learning, flooding și forwarding unicast/broadcast
- **`Link`** — Conexiune punct-la-punct între două dispozitive

---

## MiniNet.Server

Server ASP.NET Core care orchestrează simularea rețelei și permite conectarea clienților reali.

### Tehnologii

- **ASP.NET Core** + **SignalR** pentru comunicare real-time
- **Entity Framework Core** + **SQLite** pentru persistarea topologiei și evenimentelor
- Fișier bază de date: `mininet.db`

### API REST

| Endpoint | Descriere |
|----------|-----------|
| `GET /api/topology` | Topologia curentă (noduri și conexiuni) |
| `GET /api/events` | Ultimele 200 de evenimente de rețea |

### Hub SignalR (`/networkHub`)

Metode disponibile clienților:

| Metodă | Descriere |
|--------|-----------|
| `RegisterHostAuto(name, switchName)` | Înregistrare cu alocare automată IP/MAC |
| `RegisterHost(dto)` | Înregistrare cu IP/MAC specificat |
| `SendFrame(dto)` | Trimitere frame de rețea |
| `JoinDashboard()` | Abonare la evenimente și topologie |
| `CreateSwitch(name)` | Creare switch nou |
| `CreateRouter(name)` | Creare router nou |
| `CreateDevice(dto)` | Adăugare dispozitiv simulat |
| `DeleteSwitch/Router/Device(name)` | Ștergere entitate |
| `GetSwitches()` | Lista switch-urilor disponibile |

### Componente principale

**`NetworkService`** — Orchestratorul central care:
- Menține tabelele MAC pe fiecare switch
- Rutează frame-uri între clienți reali, hosturi simulate și routere
- Alocă dinamic IP-uri în subnete (CIDR /24)
- Persistă topologia și evenimentele în SQLite
- Broadcastează topologia și evenimentele la dashboard

**`RouterLogic`** — Implementare router Layer 3:
- Tabel ARP intern
- Rutare: subnete direct conectate + rute statice (longest prefix match)
- Decrementare TTL; generare ICMP Time Exceeded la TTL ≤ 1

**`SimulatedHost`** — Dispozitiv virtual care răspunde la:
- ARP Request
- ICMP Echo Request → Echo Reply
- TCP: SYN → SYN-ACK, DATA → ACK, FIN → FIN-ACK

### Configurare (`appsettings.json`)

```json
{
  "Network": {
    "Switches": [
      { "Name": "Switch1" },
      { "Name": "Switch2" }
    ],
    "Routers": [
      {
        "Name": "Router1",
        "Interfaces": [
          {
            "Name": "eth0",
            "Mac": "AA:00:01:00:00:01",
            "Ip": "10.0.0.1",
            "PrefixLength": 24,
            "Switch": "Switch1"
          },
          {
            "Name": "eth1",
            "Mac": "AA:00:01:00:00:02",
            "Ip": "10.1.0.1",
            "PrefixLength": 24,
            "Switch": "Switch2"
          }
        ],
        "Routes": []
      }
    ]
  }
}
```

---

## MiniNet.Client

Client CLI care conectează utilizatorul la server ca dispozitiv real în rețea.

### Pornire

```bash
cd MiniNet.Client
dotnet run [url-server]   # default: http://localhost:5000
```

La prima rulare, un wizard interactiv permite alegerea sau crearea unui switch și introducerea unui nume pentru dispozitiv. Serverul alocă automat IP și MAC.

### Comenzi disponibile

| Comandă | Descriere |
|---------|-----------|
| `ping <ip>` | 4 ICMP Echo Request cu afișare RTT |
| `traceroute <ip>` | Urmărire rută cu TTL crescător |
| `tcp <ip> <port> <data>` | TCP 3-way handshake + transfer date + FIN |
| `arp` | Afișare tabel ARP local |
| `exit` / `quit` | Deconectare |

### Exemplu sesiune

```
> ping 10.1.0.10
PING 10.1.0.10
64 bytes from 10.1.0.10: icmp_seq=1 time=12ms
64 bytes from 10.1.0.10: icmp_seq=2 time=10ms
...

> traceroute 10.1.0.10
 1  10.0.0.1  8ms
 2  10.1.0.10  15ms

> tcp 10.1.0.10 80 "GET /"
[SYN] → [SYN-ACK] → [ACK] → Connected
Sending: GET /
[ACK received]
[FIN] → [FIN-ACK] → Connection closed
```

---

## MiniNet.Tests

Teste unitare xUnit pentru componentele core.

### Rulare teste

```bash
cd MiniNet.Tests
dotnet test
```

### Acoperire

| Clasă testată | Scenarii |
|---------------|----------|
| `Host` | Drop unicast fără destinație, broadcast, ARP Request/Reply, validare MAC/IP |
| `Switch` | MAC learning, forward unicast cunoscut, flood necunoscut, broadcast, excludere sender |

---

## Cerințe

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Pornire rapidă

```bash
# Clonare și build
git clone <repo-url>
cd MiniNet
dotnet build

# Pornire server
cd MiniNet.Server
dotnet run

# Conectare client (terminal nou)
cd MiniNet.Client
dotnet run
```

---

## Arhitectura rețelei simulate

```
┌─────────────────────────────────────────────┐
│                MiniNet.Server               │
│                                             │
│  Client1 ──┐                               │
│  Client2 ──┤── Switch1 ──┐                 │
│  SimHost1 ─┘             │                 │
│                      Router1               │
│  SimHost2 ─┐             │                 │
│  Client3 ──┴── Switch2 ──┘                 │
│                                             │
│  Dashboard (SignalR) ← Topology + Events    │
└─────────────────────────────────────────────┘
```

Frame-urile parcurg: Client → SignalR → NetworkService → Switch MAC table → Router (dacă inter-subnet) → destinație (client real sau SimulatedHost).

---

## Protocoale implementate

| Layer | Protocol | Funcționalitate |
|-------|----------|----------------|
| 2 | Ethernet | Frame cu MAC src/dst și EtherType |
| 2 | ARP | Rezolvare IP → MAC (Request/Reply) |
| 3 | IPv4 | Rutare cu TTL, forward inter-subnet |
| 3 | ICMP | Echo Request/Reply, Time Exceeded |
| 4 | TCP | 3-way handshake, transfer date, FIN, retry logic |
