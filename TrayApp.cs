using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ShareKM;

sealed class Config
{
    public string Peer { get; set; } = "";
    public int Port { get; set; } = 24800;
    public string Hotkey { get; set; } = "Scroll";

    public static readonly string FilePath = Environment.GetEnvironmentVariable("SHAREKM_CONFIG") is { Length: > 0 } f ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShareKM", "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        var c = new Config();
        if (!File.Exists(FilePath)) c.Save();
        return c;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}

// Hotkey rule, same on both PCs: if this PC is forwarding -> stop; if the other PC is controlling this one ->
// tell it to stop (release); otherwise start forwarding. So the hotkey on either keyboard always ends a session.
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int MaxClip = 1024 * 1024, VkEscape = 0x1B;

    readonly Config _cfg = Config.Load();
    readonly Link _link = new();
    readonly Discovery _discovery = new();
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _status, _switch, _connectTo, _autostart;
    readonly ClipWindow _clipWindow;
    readonly SynchronizationContext _ui;
    readonly Native.HookProc _kbProc, _mouseProc;
    readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 100 };
    readonly Dictionary<Color, Icon> _icons = [];
    readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    readonly HashSet<int> _buttonsDown = [];
    IntPtr _kbHook, _mouseHook;
    Keys _hotkey;
    int _hotkeyVk, _swallowUpVk;
    bool _forwarding, _remoteControlling, _hotkeyHeld, _connected;
    long _hotkeyAt, _mouseAt, _escSince;
    Native.POINT _lock;
    Action<Keys?>? _record;
    SettingsForm? _settings;
    string? _lastText, _lastError;

    public TrayApp(bool showSettings = false)
    {
        _hotkey = Enum.TryParse<Keys>(_cfg.Hotkey, true, out var k) && (k & Keys.KeyCode) != Keys.None
            ? k & Keys.KeyCode : Keys.Scroll;
        _hotkeyVk = (int)_hotkey;

        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("Not connected") { Enabled = false };
        _switch = new ToolStripMenuItem("", null, (_, _) => OnHotkey());
        _connectTo = new ToolStripMenuItem("Connect to");
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => SetAutostart(!_autostart!.Checked))
            { Checked = IsAutostart() };
        menu.Items.AddRange([
            _status, new ToolStripSeparator(), _switch, _connectTo,
            new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()),
            _autostart, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Quit()),
        ]);
        menu.Opening += (_, _) => FillConnectMenu();
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _clipWindow = new ClipWindow(OnLocalClipboard);

        _kbProc = KeyboardHook;
        _mouseProc = MouseHook;
        InstallHooks();
        _watchdog.Tick += (_, _) => Watchdog();
        _watchdog.Start();

        _link.ConnectionChanged += up => _ui.Post(_ => OnConnection(up), null);
        _link.ClipboardReceived += text => _ui.Post(_ => OnRemoteClipboard(text), null);
        _link.Error += msg => _ui.Post(_ => ShowError(msg), null);
        _link.RemoteControlChanged += on => _ui.Post(_ => OnRemoteControl(on), null);
        _link.ReleaseRequested += () => _ui.Post(_ => OnReleaseRequested(), null);
        _discovery.Error += msg => _ui.Post(_ => ShowError(msg), null);
        UpdateUi();
        _link.Start(_cfg.Port, _cfg.Peer);
        _discovery.Start(_cfg.Port);

        if (_kbHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            Balloon("ShareKM", "Could not install the keyboard/mouse hooks; switching will not work.", ToolTipIcon.Error);
        else if (string.IsNullOrWhiteSpace(_cfg.Peer))
            Balloon("ShareKM is running", "Double-click the tray icon to pick the other computer.");
        if (showSettings) ShowSettings();
    }

    // ---- used by the settings window ----

    public Keys Hotkey => _hotkey;
    public bool Connected => _link.Connected;
    public bool Paused => _link.Paused;
    public string Peer => _cfg.Peer;
    public string? PeerEndpoint => _link.PeerEndpoint;
    public IReadOnlyList<Discovery.Peer> Peers => _discovery.Peers;
    public string Status => StatusText();

    public void SetHotkey(Keys key)
    {
        _hotkey = key;
        _hotkeyVk = (int)key;
        _hotkeyHeld = false;
        _cfg.Hotkey = key.ToString();
        _cfg.Save();
        UpdateUi();
    }

    // The next physical key press goes to done (null = Esc, cancelled) instead of doing anything else.
    public void BeginRecord(Action<Keys?> done) => _record = done;
    public void CancelRecord() => _record = null;

    public void ConnectTo(string address)
    {
        address = address.Trim();
        if (address.Length == 0) return;
        _cfg.Peer = address;
        _cfg.Save();
        bool same = _link.Connected && _link.PeerEndpoint == address;
        _link.SetPaused(false);
        if (!same) _link.SetPeer(address);
        UpdateUi();
    }

    public void Disconnect()
    {
        StopForwarding(false);
        _link.SetPaused(true);
        UpdateUi();
    }

    public void Resume()
    {
        _link.SetPaused(false);
        UpdateUi();
    }

    public static string KeyName(Keys k) => k switch
    {
        Keys.Scroll => "Scroll Lock",
        Keys.Capital => "Caps Lock",
        Keys.NumLock => "Num Lock",
        Keys.Snapshot => "Print Screen",
        Keys.Next => "Page Down",
        Keys.Prior => "Page Up",
        Keys.Return => "Enter",
        _ => k.ToString(),
    };

    // ---- switching ----

    void OnHotkey()
    {
        if (_forwarding) StopForwarding(true);
        else if (_remoteControlling)
        {
            _link.SendRelease();
            _remoteControlling = false;
            UpdateUi();
            Balloon("Control returned", "The other computer's keyboard and mouse control it again.");
        }
        else if (_link.Connected) StartForwarding();
        else Balloon("ShareKM", "Not connected to the other computer", ToolTipIcon.Warning);
    }

    void StartForwarding()
    {
        Native.GetCursorPos(out _lock);
        _forwarding = true;
        _mouseAt = Environment.TickCount64;
        _escSince = 0;
        _link.SendForwarding(true);
        UpdateUi();
        Balloon("Controlling the other computer", $"Press {KeyName(_hotkey)} on either keyboard to come back (or hold Esc 3 s).");
    }

    void StopForwarding(bool notify)
    {
        if (!_forwarding) return;
        foreach (var (vk, scan, ext) in _keysDown) _link.SendKey(vk, scan, ext, true);
        foreach (var b in _buttonsDown) _link.SendButton(b, false);
        _keysDown.Clear();
        _buttonsDown.Clear();
        _forwarding = false;
        _escSince = 0;
        _link.SendForwarding(false);
        UpdateUi();
        if (notify) Balloon("Controlling this computer", $"Press {KeyName(_hotkey)} to switch again.");
    }

    void OnRemoteControl(bool on)
    {
        if (on) StopForwarding(false); // only one side controls at a time
        if (on == _remoteControlling) return;
        _remoteControlling = on;
        UpdateUi();
        if (on) Balloon("Controlled by the other computer", $"Press {KeyName(_hotkey)} here to give control back.");
    }

    void OnReleaseRequested()
    {
        if (!_forwarding) return;
        StopForwarding(false);
        Balloon("Controlling this computer", "The hotkey was pressed on the other computer.");
    }

    void OnConnection(bool up)
    {
        if (up == _connected) { UpdateUi(); return; }
        _connected = up;
        if (!up)
        {
            StopForwarding(false);
            _remoteControlling = false;
        }
        UpdateUi();
        if (up) Balloon("Connected", $"Linked with {PeerLabel()}. Press {KeyName(_hotkey)} to switch.");
        else Balloon("Disconnected", "Lost the link to the other computer.", ToolTipIcon.Warning);
    }

    // ---- hooks (run on the UI thread; must return fast) ----

    void InstallHooks()
    {
        if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        var mod = Native.GetModuleHandle(null);
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    // Windows silently removes a low-level hook that once took too long. Detect that and recover:
    // - the hotkey is physically down but the keyboard hook did not see it -> reinstall, stop forwarding;
    // - while forwarding, the cursor left the lock point and the mouse hook is silent -> reinstall;
    // - Esc held 3 s while forwarding -> stop forwarding (failsafe that does not depend on the hotkey).
    void Watchdog()
    {
        long now = Environment.TickCount64;
        if (_record == null && Native.GetAsyncKeyState(_hotkeyVk) < 0 && now - _hotkeyAt > 1000)
        {
            _hotkeyAt = now;
            InstallHooks();
            StopForwarding(false);
            Balloon("ShareKM", "The keyboard hook was lost and has been reinstalled. Controlling this computer.", ToolTipIcon.Warning);
            return;
        }
        if (!_forwarding) return;
        if (_escSince != 0 && now - _escSince > 3000)
        {
            StopForwarding(false);
            Balloon("Controlling this computer", "Esc was held for 3 seconds.");
            return;
        }
        if (now - _mouseAt > 1000 && Native.GetCursorPos(out var p) && (p.x != _lock.x || p.y != _lock.y))
        {
            _mouseAt = now;
            InstallHooks();
            Native.SetCursorPos(_lock.x, _lock.y);
            if (_mouseHook == IntPtr.Zero) StopForwarding(true);
        }
    }

    static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & Native.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                if (_record != null && down && !IsModifier(k.vkCode))
                {
                    var done = _record;
                    _record = null;
                    _swallowUpVk = (int)k.vkCode;
                    Keys? key = k.vkCode == VkEscape ? null : (Keys)k.vkCode;
                    _ui.Post(_ => done(key), null);
                    return 1;
                }
                if (_swallowUpVk != 0 && k.vkCode == _swallowUpVk)
                {
                    if (!down) _swallowUpVk = 0;
                    return 1;
                }
                if (_record == null && k.vkCode == _hotkeyVk)
                {
                    long now = Environment.TickCount64;
                    if (down)
                    {
                        // Held repeats arrive every ~30 ms; a gap > 500 ms means a new press even if a key-up was missed.
                        if (!_hotkeyHeld || now - _hotkeyAt > 500) _ui.Post(_ => OnHotkey(), null);
                        _hotkeyHeld = true;
                    }
                    else _hotkeyHeld = false;
                    _hotkeyAt = now;
                    return 1;
                }
                if (_forwarding)
                {
                    if (k.vkCode == VkEscape)
                        _escSince = down ? (_escSince == 0 ? Environment.TickCount64 : _escSince) : 0;
                    var key = ((ushort)k.vkCode, (ushort)k.scanCode, (k.flags & Native.LLKHF_EXTENDED) != 0);
                    if (down) _keysDown.Add(key);
                    else if (!_keysDown.Remove(key)) goto pass; // pressed before switching: release it here
                    _link.SendKey(key.Item1, key.Item2, key.Item3, !down);
                    return 1;
                }
            }
        }
    pass:
        return Native.CallNextHookEx(_kbHook, code, wParam, lParam);
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_forwarding) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
        _mouseAt = Environment.TickCount64;
        var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        if ((m.flags & Native.LLMHF_INJECTED) != 0) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);

        int msg = (int)wParam;
        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                int dx = m.pt.x - _lock.x, dy = m.pt.y - _lock.y;
                if (dx != 0 || dy != 0) _link.SendMove(dx, dy);
                Native.SetCursorPos(_lock.x, _lock.y);
                break;
            case Native.WM_MOUSEWHEEL:
            case Native.WM_MOUSEHWHEEL:
                _link.SendWheel((short)(m.mouseData >> 16), msg == Native.WM_MOUSEHWHEEL);
                break;
            default:
                int id = msg switch
                {
                    Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP => 0,
                    Native.WM_RBUTTONDOWN or Native.WM_RBUTTONUP => 1,
                    Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP => 2,
                    Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP => (m.mouseData >> 16) == 1 ? 3 : 4,
                    _ => -1,
                };
                if (id < 0) break;
                bool down = msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN;
                if (down) _buttonsDown.Add(id);
                else if (!_buttonsDown.Remove(id)) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
                _link.SendButton(id, down);
                break;
        }
        return 1;
    }

    // ---- clipboard ----

    async void OnLocalClipboard()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                var text = Clipboard.GetText();
                if (text.Length == 0 || text.Length >= MaxClip || text == _lastText) return;
                _lastText = text;
                _link.SendClipboard(text);
                return;
            }
            catch (ExternalException) { await Task.Delay(100); }
        }
    }

    async void OnRemoteClipboard(string text)
    {
        if (text.Length == 0 || text == _lastText) return;
        _lastText = text;
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (ExternalException) { await Task.Delay(100); } // no Thread.Sleep: it would stall the hooks
        }
    }

    // ---- tray ----

    string PeerLabel()
    {
        var ip = _link.PeerEndpoint;
        var name = _discovery.Peers.FirstOrDefault(p => p.Address.ToString() == ip)?.Name;
        return name == null ? ip ?? "?" : $"{name} ({ip})";
    }

    string StatusText() =>
        _link.Paused ? "Disconnected"
        : !_link.Connected ? "Not connected"
        : _forwarding ? $"Controlling {PeerLabel()}"
        : _remoteControlling ? $"Controlled by {PeerLabel()}"
        : $"Connected to {PeerLabel()}";

    void UpdateUi()
    {
        var status = StatusText();
        _status.Text = status;
        _switch.Text = _forwarding ? $"Switch back to this computer ({KeyName(_hotkey)})"
            : _remoteControlling ? $"Give control back ({KeyName(_hotkey)})"
            : $"Switch to other computer ({KeyName(_hotkey)})";
        var color = !_link.Connected ? Color.Gray
            : _forwarding ? Color.DarkOrange
            : _remoteControlling ? Color.DodgerBlue
            : Color.FromArgb(0x2E, 0xA8, 0x4A);
        _icon.Icon = DotIcon(color);
        var tip = "ShareKM: " + status;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        _settings?.RefreshState();
    }

    void FillConnectMenu()
    {
        _connectTo.DropDownItems.Clear();
        var current = _link.Connected ? _link.PeerEndpoint : null;
        foreach (var p in _discovery.Peers)
        {
            var ip = p.Address.ToString();
            _connectTo.DropDownItems.Add(new ToolStripMenuItem($"{p.Name} ({ip})", null, (_, _) => ConnectTo(ip))
                { Checked = ip == current });
        }
        if (_connectTo.DropDownItems.Count == 0)
            _connectTo.DropDownItems.Add(new ToolStripMenuItem("(no other computers found)") { Enabled = false });
    }

    void ShowSettings()
    {
        if (_settings == null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(this);
            _settings.FormClosed += (_, _) => { CancelRecord(); _settings = null; };
        }
        _settings.Show();
        _settings.Activate();
    }

    // App glyph tinted per state, drawn at the tray's real size so it stays crisp.
    Icon DotIcon(Color c)
    {
        if (_icons.TryGetValue(c, out var icon)) return icon;
        using var bmp = Glyph.Render(Math.Max(16, SystemInformation.SmallIconSize.Width), c);
        return _icons[c] = Icon.FromHandle(bmp.GetHicon());
    }

    public static Icon AppIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("ShareKM.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    void Balloon(string title, string text, ToolTipIcon kind = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(3000, title, text, kind);

    void ShowError(string msg)
    {
        if (msg == _lastError) return;
        _lastError = msg;
        Balloon("ShareKM", msg, ToolTipIcon.Error);
    }

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("ShareKM") is string v && v.Trim('"').Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("ShareKM", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ShareKM", false);
        }
        catch (Exception e) { ShowError("Could not change auto-start: " + e.Message); }
        _autostart.Checked = IsAutostart();
    }

    public void Quit()
    {
        StopForwarding(false);
        if (_remoteControlling) _link.SendRelease();
        _watchdog.Stop();
        if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _kbHook = _mouseHook = IntPtr.Zero;
        _settings?.Close();
        _clipWindow.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    // Message-only window that receives WM_CLIPBOARDUPDATE.
    sealed class ClipWindow : NativeWindow, IDisposable
    {
        readonly Action _changed;

        public ClipWindow(Action changed)
        {
            _changed = changed;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
            Native.AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLIPBOARDUPDATE) _changed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            Native.RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
