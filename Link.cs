using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ShareKM;

// One TCP connection between the two PCs. Both sides listen; a side with a peer address also dials out.
// Frame: int32 length, then byte type + payload. Same wire format both ways (input events and clipboard text).
sealed class Link
{
    const byte MMove = 1, MButton = 2, MWheel = 3, MKey = 4, MClip = 5, MControl = 6, MHello = 7;
    const byte CStopped = 0, CStarted = 1, CRelease = 2;
    const int MaxFrame = 4 * 1024 * 1024;

    readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>();
    readonly object _lock = new();
    readonly string _id = Guid.NewGuid().ToString("N");
    TcpClient? _client;
    bool _outgoing;
    DateTime _attachedAt;
    volatile string? _peer;
    volatile bool _paused;
    volatile TcpListener? _listener;
    int _port;

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? Error;
    // The other side started (true) or stopped (false) forwarding its keyboard/mouse to us.
    public event Action<bool>? RemoteControlChanged;
    // The other side asks us to stop forwarding (its hotkey was pressed while we controlled it).
    public event Action? ReleaseRequested;

    public bool Connected { get { lock (_lock) return _client != null; } }
    public bool Paused => _paused;

    public string? PeerEndpoint
    {
        get
        {
            try { lock (_lock) return _client == null ? null : Ip(_client.Client.RemoteEndPoint)?.ToString(); }
            catch (ObjectDisposedException) { return null; }
        }
    }

    public void Start(int port, string? peer, bool listen = true)
    {
        _port = port;
        _peer = peer;
        _ = Task.Run(WriteLoop);
        if (listen) _ = Task.Run(ListenLoop);
        _ = Task.Run(ConnectLoop);
    }

    public void SetPeer(string? peer)
    {
        _peer = peer;
        DropCurrent();
    }

    // Paused: no dialing, incoming connections refused, current one dropped.
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused) return;
        try { _listener?.Stop(); } catch { } // refuse at TCP level so the other side does not see connect/drop cycles
        DropCurrent();
    }

    void DropCurrent()
    {
        TcpClient? c;
        lock (_lock) c = _client;
        if (c != null) Detach(c);
    }

    public void SendMove(int dx, int dy) => Send(MMove, w => { w.Write(dx); w.Write(dy); });
    public void SendButton(int id, bool down) => Send(MButton, w => { w.Write((byte)id); w.Write(down); });
    public void SendWheel(int delta, bool horizontal) => Send(MWheel, w => { w.Write(delta); w.Write(horizontal); });
    public void SendKey(ushort vk, ushort scan, bool extended, bool up) =>
        Send(MKey, w => { w.Write(vk); w.Write(scan); w.Write(extended); w.Write(up); });
    public void SendClipboard(string text) => Send(MClip, w => w.Write(text));
    public void SendForwarding(bool on) => Send(MControl, w => w.Write(on ? CStarted : CStopped));
    public void SendRelease() => Send(MControl, w => w.Write(CRelease));

    void Send(byte type, Action<BinaryWriter> body)
    {
        if (!Connected) return;
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(0); // length placeholder
            w.Write(type);
            body(w);
        }
        var frame = ms.ToArray();
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), frame.Length - 4);
        _out.Writer.TryWrite(frame);
    }

    async Task WriteLoop()
    {
        await foreach (var frame in _out.Reader.ReadAllAsync())
        {
            TcpClient? c;
            lock (_lock) c = _client;
            if (c == null) continue;
            try { await c.GetStream().WriteAsync(frame); }
            catch { Detach(c); }
        }
    }

    async Task ListenLoop()
    {
        while (true)
        {
            if (_paused) { await Task.Delay(500); continue; }
            TcpListener? listener = null;
            try
            {
                listener = TcpListener.Create(_port); // dual-mode: IPv4 and IPv6 (hostnames often resolve to IPv6 first)
                listener.Start();
                _listener = listener;
                while (!_paused) Attach(await listener.AcceptTcpClientAsync(), false);
            }
            catch (Exception e) when (!_paused)
            {
                Error?.Invoke($"Cannot listen on port {_port}: {e.Message}");
                await Task.Delay(5000);
            }
            catch { }
            finally
            {
                _listener = null;
                try { listener?.Stop(); } catch { }
            }
        }
    }

    async Task ConnectLoop()
    {
        while (true)
        {
            var peer = _peer;
            if (string.IsNullOrWhiteSpace(peer) || Connected || _paused)
            {
                await Task.Delay(1500);
                continue;
            }
            var c = new TcpClient();
            try
            {
                await c.ConnectAsync(peer.Trim(), _port).WaitAsync(TimeSpan.FromSeconds(3));
                Attach(c, true);
            }
            catch
            {
                c.Dispose();
                await Task.Delay(3000);
            }
        }
    }

    void Attach(TcpClient c, bool outgoing)
    {
        if (_paused) { c.Dispose(); return; }
        try
        {
            c.NoDelay = true;
            // Detect a vanished peer (sleep, Wi-Fi drop) in ~10 s instead of the 2 h default.
            c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch
        {
            c.Dispose();
            return;
        }
        TcpClient? old;
        lock (_lock)
        {
            old = _client;
            if (old != null && DateTime.UtcNow - _attachedAt < TimeSpan.FromSeconds(5) && !KeepNew(old, _outgoing, c, outgoing))
            {
                c.Dispose();
                return;
            }
            _client = c;
            _outgoing = outgoing;
            _attachedAt = DateTime.UtcNow;
        }
        old?.Dispose();
        Send(MHello, w => w.Write(_id));
        ConnectionChanged?.Invoke(true);
        _ = Task.Run(() => ReadLoop(c));
    }

    // Both PCs dialed each other at the same time: both sides must keep the same socket, so keep the one
    // dialed by the lower IP address. Any other case (e.g. the peer restarted): the new connection wins.
    static bool KeepNew(TcpClient old, bool oldOut, TcpClient c, bool cOut)
    {
        try
        {
            if (oldOut == cOut || !Equals(Ip(old.Client.RemoteEndPoint), Ip(c.Client.RemoteEndPoint))) return true;
            var dOld = Ip(oldOut ? old.Client.LocalEndPoint : old.Client.RemoteEndPoint)!.GetAddressBytes();
            var dNew = Ip(cOut ? c.Client.LocalEndPoint : c.Client.RemoteEndPoint)!.GetAddressBytes();
            return ((ReadOnlySpan<byte>)dNew).SequenceCompareTo(dOld) <= 0;
        }
        catch { return true; }
    }

    static IPAddress? Ip(EndPoint? ep) => ep is IPEndPoint ip
        ? ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address
        : null;

    void Detach(TcpClient c)
    {
        bool wasCurrent;
        lock (_lock)
        {
            wasCurrent = ReferenceEquals(_client, c);
            if (wasCurrent) _client = null;
        }
        c.Dispose();
        if (wasCurrent) ConnectionChanged?.Invoke(false);
    }

    async Task ReadLoop(TcpClient c)
    {
        try
        {
            var s = c.GetStream();
            var head = new byte[4];
            while (true)
            {
                await s.ReadExactlyAsync(head);
                int len = BitConverter.ToInt32(head);
                if (len < 1 || len > MaxFrame) break;
                var body = new byte[len];
                await s.ReadExactlyAsync(body);
                if (!Dispatch(body)) break;
            }
        }
        catch { }
        Detach(c);
    }

    bool Dispatch(byte[] body)
    {
        using var r = new BinaryReader(new MemoryStream(body));
        switch (r.ReadByte())
        {
            case MMove: Native.InjectMove(r.ReadInt32(), r.ReadInt32()); break;
            case MButton: Native.InjectButton(r.ReadByte(), r.ReadBoolean()); break;
            case MWheel: Native.InjectWheel(r.ReadInt32(), r.ReadBoolean()); break;
            case MKey: Native.InjectKey(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean()); break;
            case MClip: ClipboardReceived?.Invoke(r.ReadString()); break;
            case MControl:
                var s = r.ReadByte();
                if (s == CRelease) ReleaseRequested?.Invoke();
                else RemoteControlChanged?.Invoke(s == CStarted);
                break;
            case MHello:
                if (r.ReadString() != _id) break;
                // Dialed our own listener (the address is this PC): stop dialing it.
                Error?.Invoke($"{_peer} is this computer. Enter the other computer's address.");
                _peer = null;
                return false;
        }
        return true;
    }
}
