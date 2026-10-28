using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ShareKM;

// LAN discovery: every instance broadcasts a beacon every 2 s on UDP 24801 and lists the others seen in the last 8 s.
// Beacon: "SHAREKM1|<instance id>|<tcp port>|<computer name>" in UTF-8.
sealed class Discovery
{
    public const int DefaultPort = 24801;
    const string Sig = "SHAREKM1";
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(8);

    public sealed record Peer(string Id, string Name, IPAddress Address, int TcpPort, DateTime Seen);

    public string Id { get; } = Guid.NewGuid().ToString("N");
    readonly ConcurrentDictionary<string, Peer> _peers = new();
    public event Action<string>? Error;

    public IReadOnlyList<Peer> Peers =>
        [.. _peers.Values.Where(p => DateTime.UtcNow - p.Seen < Ttl).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    public static byte[] Encode(string id, string name, int tcpPort) =>
        Encoding.UTF8.GetBytes($"{Sig}|{id}|{tcpPort}|{name}");

    public static bool TryDecode(ReadOnlySpan<byte> data, out string id, out string name, out int tcpPort)
    {
        id = name = "";
        tcpPort = 0;
        if (data.Length > 512) return false;
        var parts = Encoding.UTF8.GetString(data).Split('|', 4);
        if (parts.Length != 4 || parts[0] != Sig || parts[1].Length == 0 || !int.TryParse(parts[2], out tcpPort)) return false;
        id = parts[1];
        name = parts[3].Length > 64 ? parts[3][..64] : parts[3];
        return true;
    }

    public void Start(int tcpPort, int udpPort = DefaultPort)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        try { udp.Client.IOControl(-1744830452, [0, 0, 0, 0], null); } catch { } // SIO_UDP_CONNRESET off
        try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort)); }
        catch (Exception e) { Error?.Invoke($"Discovery cannot use UDP port {udpPort}: {e.Message}"); }
        _ = Task.Run(() => ReceiveLoop(udp));
        _ = Task.Run(() => BeaconLoop(udp, tcpPort, udpPort));
    }

    async Task BeaconLoop(UdpClient udp, int tcpPort, int udpPort)
    {
        var beacon = Encode(Id, Environment.MachineName, tcpPort);
        while (true)
        {
            var targets = LocalAddresses().Select(a => Broadcast(a.Ip, a.Mask)).Append(IPAddress.Broadcast).Distinct();
            foreach (var t in targets)
                try { await udp.SendAsync(beacon, new IPEndPoint(t, udpPort)); } catch { }
            await Task.Delay(2000);
        }
    }

    async Task ReceiveLoop(UdpClient udp)
    {
        while (true)
        {
            try
            {
                var r = await udp.ReceiveAsync();
                if (TryDecode(r.Buffer, out var id, out var name, out var port) && id != Id)
                    _peers[id] = new Peer(id, name, r.RemoteEndPoint.Address, port, DateTime.UtcNow);
            }
            catch (ObjectDisposedException) { return; }
            catch { await Task.Delay(500); } // e.g. ICMP port unreachable surfaced as ConnectionReset
        }
    }

    static IPAddress Broadcast(IPAddress ip, IPAddress mask)
    {
        var b = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++) b[i] |= (byte)~m[i];
        return new IPAddress(b);
    }

    static readonly string[] VirtualHints = ["virtual", "vmware", "hyper-v", "vethernet", "wsl", "docker", "tap-", "tunnel", "bluetooth", "loopback", "npcap"];

    // LAN IPv4 addresses of real, active adapters; adapters with a default gateway first.
    public static List<(IPAddress Ip, IPAddress Mask)> LocalAddresses()
    {
        var list = new List<(IPAddress, IPAddress, bool)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VirtualHints.Any(desc.Contains)) continue;
                var props = ni.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = u.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    list.Add((u.Address, u.IPv4Mask, gw));
                }
            }
        }
        catch { }
        return [.. list.OrderByDescending(x => x.Item3).Select(x => (x.Item1, x.Item2))];
    }
}
