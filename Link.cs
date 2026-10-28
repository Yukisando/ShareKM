using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace ShareKM;

enum LinkIssue { None, NoPassphrase, Mismatch, OldPeer, NewerPeer }

// Where received input goes: SendInput in the app, a recorder in tests.
interface IInputSink
{
    void Move(int dx, int dy);
    void Button(int id, bool down);
    void Wheel(int delta, bool horizontal);
    void Key(ushort vk, ushort scan, bool extended, bool up);
}

sealed class NativeInput : IInputSink
{
    public void Move(int dx, int dy) => Native.InjectMove(dx, dy);
    public void Button(int id, bool down) => Native.InjectButton(id, down);
    public void Wheel(int delta, bool horizontal) => Native.InjectWheel(delta, horizontal);
    public void Key(ushort vk, ushort scan, bool extended, bool up) => Native.InjectKey(vk, scan, extended, up);
}

// One TCP connection between the two PCs. Both sides listen; a side with a peer address also dials out.
// Every connection starts with a handshake that proves both sides know the shared passphrase (see Handshake);
// after it, every frame is AES-256-GCM: int32 length | uint64 counter | ciphertext | tag.
// Plaintext inside a frame: byte type + payload (input events, clipboard text, control messages).
sealed class Link
{
    const byte MMove = 1, MButton = 2, MWheel = 3, MKey = 4, MClip = 5, MControl = 6;
    const byte CStopped = 0, CStarted = 1, CRelease = 2;
    public const byte MHelloV1 = 7, MHello = 8, MAuth = 9, Version = 2;
    public const int MaxFrame = 4 * 1024 * 1024, MaxHandshakeFrame = 128, HelloSize = 1 + 4 + 1 + 32 + 16;
    public static readonly byte[] Magic = "SKM2"u8.ToArray();
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    sealed class Session(TcpClient client, bool outgoing, byte[] txKey, byte[] rxKey, byte[] master) : IDisposable
    {
        public readonly TcpClient Client = client;
        public readonly bool Outgoing = outgoing;
        public readonly byte[] Master = master;
        public readonly DateTime At = DateTime.UtcNow;
        public readonly AesGcm Tx = new(txKey, LinkCrypto.TagSize), Rx = new(rxKey, LinkCrypto.TagSize);
        public ulong TxCounter, RxCounter;
        public void Dispose() { Client.Dispose(); Tx.Dispose(); Rx.Dispose(); }
    }

    sealed class HandshakeFailed(LinkIssue issue, string? message = null) : Exception(message)
    {
        public LinkIssue Issue => issue;
    }

    readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>();
    readonly object _lock = new();
    readonly byte[] _id = Guid.NewGuid().ToByteArray();
    readonly SemaphoreSlim _handshakes = new(4);
    readonly Dictionary<IPAddress, (int Fails, DateTime Until)> _penalty = [];
    Session? _current;
    volatile byte[]? _master;
    volatile string? _peer;
    volatile bool _paused;
    volatile TcpListener? _listener;
    volatile LinkIssue _issue = LinkIssue.NoPassphrase;
    int _port;
    IPAddress? _listenOn;

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? Error;
    public event Action<LinkIssue>? IssueChanged;
    // The other side started (true) or stopped (false) forwarding its keyboard/mouse to us.
    public event Action<bool>? RemoteControlChanged;
    // The other side asks us to stop forwarding (its hotkey was pressed while we controlled it).
    public event Action? ReleaseRequested;

    public IInputSink Input { get; init; } = new NativeInput();

    public bool Connected { get { lock (_lock) return _current != null; } }
    public bool Paused => _paused;
    public bool HasKey => _master != null;
    public LinkIssue Issue => _issue;

    public string? PeerEndpoint
    {
        get
        {
            try { lock (_lock) return _current == null ? null : Ip(_current.Client.Client.RemoteEndPoint)?.ToString(); }
            catch (ObjectDisposedException) { return null; }
        }
    }

    // listenOn: null = all interfaces (the app); tests pass IPAddress.Loopback.
    public void Start(int port, string? peer, bool listen = true, IPAddress? listenOn = null)
    {
        _port = port;
        _listenOn = listenOn;
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

    // The 32-byte master key from Pairing.DeriveMaster, or null: then nothing is accepted or dialed.
    // A new key drops the current link so it is re-established (and re-keyed) with the new one.
    public void SetKey(byte[]? master)
    {
        _master = master;
        SetIssue(master == null ? LinkIssue.NoPassphrase : LinkIssue.None);
        if (master == null) StopListening();
        lock (_penalty) _penalty.Clear();
        DropCurrent();
    }

    // Paused: no dialing, incoming connections refused, current one dropped.
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused) return;
        StopListening();
        DropCurrent();
    }

    void StopListening()
    {
        try { _listener?.Stop(); } catch { } // refuse at TCP level so the other side does not see connect/drop cycles
    }

    void DropCurrent()
    {
        Session? s;
        lock (_lock) s = _current;
        if (s != null) Detach(s);
    }

    void SetIssue(LinkIssue issue)
    {
        if (_issue == issue) return;
        _issue = issue;
        IssueChanged?.Invoke(issue);
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
            w.Write(type);
            body(w);
        }
        _out.Writer.TryWrite(ms.ToArray());
    }

    // Single consumer: frames are sealed here, so counters follow the order they are written in.
    async Task WriteLoop()
    {
        await foreach (var plain in _out.Reader.ReadAllAsync())
        {
            Session? s;
            lock (_lock) s = _current;
            if (s == null) continue;
            try { await s.Client.GetStream().WriteAsync(LinkCrypto.Seal(s.Tx, s.TxCounter++, plain)); }
            catch { Detach(s); }
        }
    }

    async Task ListenLoop()
    {
        while (true)
        {
            if (_paused || _master == null) { await Task.Delay(500); continue; }
            TcpListener? listener = null;
            try
            {
                // dual-mode: IPv4 and IPv6 (hostnames often resolve to IPv6 first)
                listener = _listenOn == null ? TcpListener.Create(_port) : new TcpListener(_listenOn, _port);
                listener.Start();
                _listener = listener;
                while (!_paused && _master != null) Accept(await listener.AcceptTcpClientAsync());
            }
            catch (Exception e) when (!_paused && _master != null)
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

    // Unauthenticated peers get at most 4 handshakes at once, and an address that keeps failing is refused
    // for a growing time (5 s after the third failure, doubling up to 60 s).
    void Accept(TcpClient c)
    {
        var ip = Ip(c.Client.RemoteEndPoint);
        lock (_penalty)
            if (ip != null && _penalty.TryGetValue(ip, out var p) && p.Until > DateTime.UtcNow) { c.Dispose(); return; }
        if (!_handshakes.Wait(0)) { c.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await Handshake(c, false);
                if (ip != null) lock (_penalty) _penalty.Remove(ip);
                Attach(s);
            }
            catch (Exception e)
            {
                if (ip != null)
                    lock (_penalty)
                    {
                        int fails = (_penalty.TryGetValue(ip, out var p) ? p.Fails : 0) + 1;
                        var wait = fails < 3 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(60, 5 << Math.Min(fails - 3, 4)));
                        _penalty[ip] = (fails, DateTime.UtcNow + wait);
                    }
                Report(e, dialer: false);
                await Task.Delay(1000); // slow down guessing
                c.Dispose();
            }
            finally { _handshakes.Release(); }
        });
    }

    async Task ConnectLoop()
    {
        while (true)
        {
            var peer = _peer;
            if (string.IsNullOrWhiteSpace(peer) || Connected || _paused || _master == null)
            {
                await Task.Delay(1500);
                continue;
            }
            var c = new TcpClient();
            try
            {
                await c.ConnectAsync(peer.Trim(), _port).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                c.Dispose();
                await Task.Delay(3000);
                continue;
            }
            try
            {
                Attach(await Handshake(c, true));
            }
            catch (Exception e)
            {
                c.Dispose();
                Report(e, dialer: true);
                await Task.Delay(10_000); // auth failures retry slowly
            }
        }
    }

    void Report(Exception e, bool dialer)
    {
        switch (e)
        {
            case HandshakeFailed { Issue: LinkIssue.None } h when h.Message.Length > 0:
                // dialed our own listener (the address is this PC): stop dialing it
                if (dialer)
                {
                    Error?.Invoke($"{_peer} is this computer. Enter the other computer's address.");
                    _peer = null;
                }
                break;
            case HandshakeFailed h: SetIssue(h.Issue); break;
            // The dialer authenticates first, so a listener that hangs up or times out means a wrong passphrase
            // (or not ShareKM). On the listening side a dropped or silent peer says nothing about us.
            case EndOfStreamException or IOException or OperationCanceledException or SocketException when dialer:
                SetIssue(LinkIssue.Mismatch); break;
        }
    }

    public static byte[] Hello(byte[] nonce, byte[] id)
    {
        var b = new byte[HelloSize];
        b[0] = MHello;
        Magic.CopyTo(b, 1);
        b[5] = Version;
        nonce.CopyTo(b, 6);
        id.CopyTo(b, 38);
        return b;
    }

    // Handshake, both sides send HELLO {type 8, "SKM2", version 2, 32-byte nonce, 16-byte instance id} at once.
    // keys = HKDF-SHA256(master, salt = dialer nonce || listener nonce): dialer->listener, listener->dialer, auth.
    // The dialer then sends AUTH = HMAC-SHA256(auth, "ShareKM v2 dialer" || transcript); the listener verifies it
    // and only then answers with its own AUTH ("ShareKM v2 listener"), so a stranger who connects learns nothing
    // that could be tested offline. transcript = dialer HELLO || listener HELLO. Everything within 5 s.
    async Task<Session> Handshake(TcpClient c, bool dialer)
    {
        var master = _master ?? throw new HandshakeFailed(LinkIssue.NoPassphrase);
        using var cts = new CancellationTokenSource(HandshakeTimeout);
        var ct = cts.Token;
        c.NoDelay = true;
        var s = c.GetStream();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var mine = Hello(nonce, _id);
        await WriteFrame(s, mine, ct);
        var theirs = await ReadFrame(s, ct);
        if (theirs.Length > 0 && theirs[0] == MHelloV1) throw new HandshakeFailed(LinkIssue.OldPeer);
        if (theirs.Length != HelloSize || theirs[0] != MHello || !theirs.AsSpan(1, 4).SequenceEqual(Magic))
            throw new HandshakeFailed(LinkIssue.Mismatch);
        if (theirs[5] != Version) throw new HandshakeFailed(theirs[5] < Version ? LinkIssue.OldPeer : LinkIssue.NewerPeer);
        if (theirs.AsSpan(38, 16).SequenceEqual(_id)) throw new HandshakeFailed(LinkIssue.None, "self");

        byte[] helloD = dialer ? mine : theirs, helloL = dialer ? theirs : mine;
        var (dl, ld, auth) = LinkCrypto.SessionKeys(master, [.. helloD.AsSpan(6, 32), .. helloL.AsSpan(6, 32)]);
        byte[] transcript = [.. helloD, .. helloL];
        var tagD = LinkCrypto.AuthTag(auth, LinkCrypto.LabelDialer, transcript);
        var tagL = LinkCrypto.AuthTag(auth, LinkCrypto.LabelListener, transcript);
        if (dialer)
        {
            await WriteFrame(s, [MAuth, .. tagD], ct);
            if (!IsAuth(await ReadFrame(s, ct), tagL)) throw new HandshakeFailed(LinkIssue.Mismatch);
        }
        else
        {
            if (!IsAuth(await ReadFrame(s, ct), tagD)) throw new HandshakeFailed(LinkIssue.Mismatch);
            await WriteFrame(s, [MAuth, .. tagL], ct);
        }
        return new Session(c, dialer, dialer ? dl : ld, dialer ? ld : dl, master);
    }

    static bool IsAuth(byte[] frame, byte[] expected) =>
        frame.Length == 1 + expected.Length && frame[0] == MAuth && CryptographicOperations.FixedTimeEquals(frame.AsSpan(1), expected);

    static async Task WriteFrame(NetworkStream s, byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct);
    }

    // Before authentication: tiny frames only, so a stranger cannot make us allocate.
    static async Task<byte[]> ReadFrame(NetworkStream s, CancellationToken ct)
    {
        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct);
        int len = BitConverter.ToInt32(head);
        if (len < 1 || len > MaxHandshakeFrame) throw new HandshakeFailed(LinkIssue.Mismatch);
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return body;
    }

    void Attach(Session c)
    {
        if (_paused || !ReferenceEquals(c.Master, _master)) { c.Dispose(); return; } // paused or re-keyed meanwhile
        try
        {
            // Detect a vanished peer (sleep, Wi-Fi drop) in ~10 s instead of the 2 h default.
            var sock = c.Client.Client;
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch
        {
            c.Dispose();
            return;
        }
        Session? old;
        lock (_lock)
        {
            old = _current;
            if (old != null && DateTime.UtcNow - old.At < TimeSpan.FromSeconds(5) && !KeepNew(old, c))
            {
                c.Dispose();
                return;
            }
            _current = c;
        }
        old?.Dispose();
        SetIssue(LinkIssue.None);
        ConnectionChanged?.Invoke(true);
        _ = Task.Run(() => ReadLoop(c));
    }

    // Both PCs dialed each other at the same time: both sides must keep the same socket, so keep the one
    // dialed by the lower IP address. Any other case (e.g. the peer restarted): the new connection wins.
    static bool KeepNew(Session old, Session c)
    {
        try
        {
            if (old.Outgoing == c.Outgoing || !Equals(Ip(old.Client.Client.RemoteEndPoint), Ip(c.Client.Client.RemoteEndPoint))) return true;
            var dOld = Ip(old.Outgoing ? old.Client.Client.LocalEndPoint : old.Client.Client.RemoteEndPoint)!.GetAddressBytes();
            var dNew = Ip(c.Outgoing ? c.Client.Client.LocalEndPoint : c.Client.Client.RemoteEndPoint)!.GetAddressBytes();
            return ((ReadOnlySpan<byte>)dNew).SequenceCompareTo(dOld) <= 0;
        }
        catch { return true; }
    }

    static IPAddress? Ip(EndPoint? ep) => ep is IPEndPoint ip
        ? ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address
        : null;

    void Detach(Session c)
    {
        bool wasCurrent;
        lock (_lock)
        {
            wasCurrent = ReferenceEquals(_current, c);
            if (wasCurrent) _current = null;
        }
        c.Dispose();
        if (wasCurrent) ConnectionChanged?.Invoke(false);
    }

    // Any frame that does not decrypt with exactly the next counter (tampered, replayed, reordered, dropped)
    // ends the connection.
    async Task ReadLoop(Session c)
    {
        try
        {
            var s = c.Client.GetStream();
            var head = new byte[4];
            while (true)
            {
                await s.ReadExactlyAsync(head);
                int len = BitConverter.ToInt32(head);
                if (len < LinkCrypto.CounterSize + LinkCrypto.TagSize + 1 || len > MaxFrame + LinkCrypto.CounterSize + LinkCrypto.TagSize) break;
                var body = new byte[len];
                await s.ReadExactlyAsync(body);
                Dispatch(LinkCrypto.Open(c.Rx, c.RxCounter++, body));
            }
        }
        catch { }
        Detach(c);
    }

    void Dispatch(byte[] body)
    {
        using var r = new BinaryReader(new MemoryStream(body));
        switch (r.ReadByte())
        {
            case MMove: Input.Move(r.ReadInt32(), r.ReadInt32()); break;
            case MButton: Input.Button(r.ReadByte(), r.ReadBoolean()); break;
            case MWheel: Input.Wheel(r.ReadInt32(), r.ReadBoolean()); break;
            case MKey: Input.Key(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean()); break;
            case MClip: ClipboardReceived?.Invoke(r.ReadString()); break;
            case MControl:
                var s = r.ReadByte();
                if (s == CRelease) ReleaseRequested?.Invoke();
                else RemoteControlChanged?.Invoke(s == CStarted);
                break;
        }
    }
}
