using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareKM;

sealed class Config
{
    public string Peer { get; set; } = "";
    public int Port { get; set; } = 24800;
    public string Hotkey { get; set; } = "Scroll";
    public int MouseRateHz { get; set; } = MotionPump.DefaultHz;
    // DPAPI-protected (current Windows user) shared passphrase, base64. See Pairing.
    public string PassphraseProtected { get; set; } = "";
    // A plain "Passphrase" typed into the file by hand is read once, then saved protected instead.
    [JsonIgnore] public string PlainPassphrase { get; set; } = "";
    // Run as administrator (opt-in). See Elevation.
    public bool RunAsAdmin { get; set; }

    public static readonly string FilePath = Environment.GetEnvironmentVariable("SHAREKM_CONFIG") is { Length: > 0 } f ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShareKM", "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath));
        }
        catch { }
        var c = new Config();
        if (!File.Exists(FilePath)) c.Save();
        return c;
    }

    // Lenient: a missing, mistyped or out-of-range value falls back to its default instead of losing the whole file.
    public static Config Parse(string json)
    {
        var c = new Config();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return c;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                switch (p.Name.ToLowerInvariant())
                {
                    case "peer" when v.ValueKind == JsonValueKind.String: c.Peer = v.GetString()!; break;
                    case "hotkey" when v.ValueKind == JsonValueKind.String: c.Hotkey = v.GetString()!; break;
                    case "port" when Int(v) is > 0 and < 65536: c.Port = Int(v); break;
                    case "mouseratehz": c.MouseRateHz = MotionPump.Clamp(Int(v)); break;
                    case "passphraseprotected" when v.ValueKind == JsonValueKind.String: c.PassphraseProtected = v.GetString()!; break;
                    case "passphrase" when v.ValueKind == JsonValueKind.String: c.PlainPassphrase = v.GetString()!; break;
                    case "runasadmin": c.RunAsAdmin = v.ValueKind == JsonValueKind.True
                        || v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b
                        || v.ValueKind == JsonValueKind.Number && Int(v) == 1; break;
                }
            }
        }
        catch (JsonException) { }
        return c;
    }

    static int Int(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d) < int.MaxValue ? (int)Math.Round(d)
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i) ? i : 0;

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
enum SecurityState { NotPaired, Applying, Waiting, Encrypted, Mismatch, OldPeer, NewerPeer }

sealed record UpdateInfo(Updater.Release? Available, bool Checking, bool Installing, int Percent, bool UpToDate, string? Error);

sealed class TrayApp : ApplicationContext, ISettingsHost
{
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
    readonly MotionPump _motion;
    readonly System.Windows.Forms.Timer _saveLater = new() { Interval = 600 };
    IntPtr _kbHook, _mouseHook;
    Keys _hotkey;
    int _hotkeyVk, _swallowUpVk;
    bool _forwarding, _remoteControlling, _hotkeyHeld, _connected;
    long _hotkeyAt, _mouseAt, _escSince;
    Native.POINT _lock;
    Action<Keys?>? _record;
    SettingsForm? _settings;
    string? _lastText, _lastError;

    public TrayApp(bool showSettings = false, bool relaunched = false)
    {
        _hotkey = Enum.TryParse<Keys>(_cfg.Hotkey, true, out var k) && (k & Keys.KeyCode) != Keys.None
            ? k & Keys.KeyCode : Keys.Scroll;
        _hotkeyVk = (int)_hotkey;
        _motion = new MotionPump(_link.SendMove) { RateHz = _cfg.MouseRateHz };
        _saveLater.Tick += (_, _) => { _saveLater.Stop(); _cfg.Save(); };

        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("Not connected") { Enabled = false };
        _switch = new ToolStripMenuItem("", null, (_, _) => OnHotkey());
        _connectTo = new ToolStripMenuItem("Connect to");
        _autostart = new ToolStripMenuItem("Start with Windows", null, async (_, _) => await SetStartWithWindows(!_autostart!.Checked));
        menu.Items.AddRange([
            _status, new ToolStripSeparator(), _switch, _connectTo,
            new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()),
            _autostart, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Quit()),
        ]);
        menu.Opening += (_, _) => FillConnectMenu();
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _icon.BalloonTipClicked += (_, _) => ShowSettings();
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
        _link.IssueChanged += issue => _ui.Post(_ => OnIssue(issue), null);
        _discovery.Error += msg => _ui.Post(_ => ShowError(msg), null);

        bool unreadable = LoadPassphrase();
        UpdateUi();
        // SHAREKM_LISTEN (like SHAREKM_CONFIG): bind to one address only, e.g. 127.0.0.1 for local trials.
        var bind = System.Net.IPAddress.TryParse(Environment.GetEnvironmentVariable("SHAREKM_LISTEN"), out var ip) ? ip : null;
        _link.Start(_cfg.Port, _cfg.Peer, listenOn: bind);
        _discovery.Start(_cfg.Port, bind: bind);
        if (_passphrase.Length > 0) _ = UseKey(_passphrase);

        if (_kbHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            Balloon("ShareKM", "Could not install the keyboard/mouse hooks; switching will not work.", ToolTipIcon.Error);
        else if (_passphrase.Length == 0)
        {
            Balloon("Set a passphrase to connect", unreadable
                ? "The saved passphrase could not be read on this PC. Enter it again in Settings."
                : "Enter the same passphrase on both PCs in Settings > Security.");
            showSettings = true;
        }
        else if (_cfg.RunAsAdmin && !Elevation.IsElevated && !relaunched)
            Balloon("Not running as administrator", "Administrator mode is on, but ShareKM started without it. Open Settings to restart it as administrator.");
        else if (string.IsNullOrWhiteSpace(_cfg.Peer))
            Balloon("ShareKM is running", "Double-click the tray icon to pick the other computer.");
        _ = InitAutostart();
        _ = UpdateLoop();
        if (showSettings) ShowSettings();
    }

    // ---- updates ----

    Updater.Release? _update;
    Version? _announced;
    bool _checking, _installing, _upToDate;
    int _updatePercent;
    string? _updateError;

    public string AppVersion => Updater.Current.ToString();
    public UpdateInfo Updates => new(_update, _checking, _installing, _updatePercent, _upToDate, _updateError);

    // Cleans up after the previous update, then checks quietly at start and once a day.
    async Task UpdateLoop()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        Updater.CleanUp();
        while (true)
        {
            await CheckForUpdates(quiet: true);
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    // Quiet checks only announce a new version (once) and keep network errors to themselves.
    public async Task CheckForUpdates(bool quiet = false)
    {
        if (_checking || _installing) return;
        _checking = true;
        _updateError = null;
        UpdateUi();
        try
        {
            var r = await Updater.Latest();
            _update = r != null && r.Version > Updater.Current ? r : null;
            _upToDate = _update == null;
            if (_update != null && quiet && _announced != _update.Version)
            {
                _announced = _update.Version;
                Balloon($"ShareKM {_update.Version} is available", "Click here or open Settings to update. Update both PCs.");
            }
        }
        catch (Exception e)
        {
            if (!quiet) _updateError = "Could not check for updates: " + e.Message;
        }
        finally
        {
            _checking = false;
            UpdateUi();
        }
    }

    // Downloads the new exe, swaps it in and restarts at the same elevation. On failure this copy keeps running.
    public async Task InstallUpdate()
    {
        if (_update is not { } r || _installing || _relaunching) return;
        _installing = true;
        _updatePercent = 0;
        _updateError = null;
        UpdateUi();
        var progress = new Progress<double>(p => { _updatePercent = (int)(p * 100); _settings?.RefreshState(); });
        var err = await Task.Run(() => Updater.Install(r, progress)) ?? Updater.StartNew();
        if (err == null)
        {
            Quit(); // the new copy is waiting for our single-instance lock
            return;
        }
        _installing = false;
        _updateError = err;
        UpdateUi();
    }

    // ---- administrator mode and startup ----

    bool _autostartOn, _relaunching;

    // An elevated copy puts "Start with Windows" on the right mechanism (task when administrator mode is on).
    async Task InitAutostart()
    {
        _autostartOn = await Task.Run(Autostart.IsEnabled);
        if (_autostartOn && Elevation.IsElevated)
        {
            var err = await Task.Run(() => Autostart.Apply(Autostart.Plan(true, _cfg.RunAsAdmin), true));
            if (err != null) ShowError(err);
        }
        UpdateUi();
    }

    public bool IsElevated => Elevation.IsElevated;
    public bool RunAsAdmin => _cfg.RunAsAdmin;
    public bool StartWithWindows => _autostartOn;
    public bool Relaunching => _relaunching;

    public async Task SetStartWithWindows(bool on)
    {
        var mech = Autostart.Plan(on, _cfg.RunAsAdmin && Elevation.IsElevated);
        var err = await Task.Run(() => Autostart.Apply(mech, Elevation.IsElevated));
        _autostartOn = await Task.Run(Autostart.IsEnabled);
        UpdateUi();
        if (err != null) ShowError(err);
    }

    // Called after the user confirmed the warning (turning on). Restarts ShareKM at the other elevation level
    // when needed; on any failure this copy keeps running and the setting is put back.
    public async Task SetRunAsAdmin(bool on)
    {
        if (_relaunching) return;
        _cfg.RunAsAdmin = on;
        _cfg.Save();
        UpdateUi();
        if (on == Elevation.IsElevated)
        {
            if (_autostartOn && on) await SetStartWithWindows(true); // already elevated: Run entry -> task
            return;
        }
        if (!on && _autostartOn) await SetStartWithWindows(true); // still elevated: task -> Run entry, while we can
        await Relaunch(elevated: on);
    }

    // Administrator mode is saved as on, but this copy is not elevated.
    public Task RestartElevated() => _relaunching || Elevation.IsElevated ? Task.CompletedTask : Relaunch(elevated: true);

    async Task Relaunch(bool elevated)
    {
        _relaunching = true;
        UpdateUi();
        try
        {
            var (result, error) = await Task.Run(() => elevated ? Elevation.StartElevated() : Elevation.StartUnelevated());
            if (result == Elevation.StartResult.Started && await Elevation.WaitForOtherInstance(8000))
            {
                Quit(); // the new copy is waiting for our single-instance lock
                return;
            }
            Elevation.ClearMarker();
            if (elevated)
            {
                _cfg.RunAsAdmin = false;
                _cfg.Save();
            }
            if (result == Elevation.StartResult.Cancelled)
                Balloon("Administrator mode not turned on", "Windows did not get permission, so ShareKM keeps running normally.");
            else
                Balloon("ShareKM could not restart", (error ?? "The new copy did not start.") + " This copy keeps running.", ToolTipIcon.Warning);
        }
        finally
        {
            _relaunching = false;
            UpdateUi();
        }
    }

    // ---- pairing ----

    string _passphrase = "";
    bool _deriving;

    // Returns true when a stored passphrase exists but cannot be decrypted (another Windows user or PC).
    bool LoadPassphrase()
    {
        if (_cfg.PlainPassphrase.Length > 0)
        {
            _passphrase = Pairing.Normalize(_cfg.PlainPassphrase);
            _cfg.PassphraseProtected = _passphrase.Length > 0 ? Pairing.Protect(_passphrase) : "";
            _cfg.PlainPassphrase = "";
            _cfg.Save();
            return false;
        }
        _passphrase = Pairing.Unprotect(_cfg.PassphraseProtected) ?? "";
        return _passphrase.Length == 0 && _cfg.PassphraseProtected.Length > 0;
    }

    async Task UseKey(string passphrase)
    {
        _deriving = true;
        UpdateUi();
        var key = await Task.Run(() => Pairing.DeriveMaster(passphrase)); // PBKDF2, ~0.5 s
        if (passphrase != _passphrase) return; // superseded by a newer Apply
        _deriving = false;
        _link.SetKey(key);
        UpdateUi();
    }

    public string Passphrase => _passphrase;

    public SecurityState Security =>
        _passphrase.Length == 0 ? SecurityState.NotPaired
        : _deriving ? SecurityState.Applying
        : _link.Connected ? SecurityState.Encrypted
        : _link.Issue switch
        {
            LinkIssue.Mismatch => SecurityState.Mismatch,
            LinkIssue.OldPeer => SecurityState.OldPeer,
            LinkIssue.NewerPeer => SecurityState.NewerPeer,
            _ => SecurityState.Waiting,
        };

    // Saves (protected) and re-keys: the current link drops and reconnects with the new key. Empty = unpair.
    public async Task<string?> ApplyPassphrase(string text)
    {
        var p = Pairing.Normalize(text);
        if (p.Length > 0 && p.Length < Pairing.MinLength)
            return $"Use at least {Pairing.MinLength} characters (Generate makes a strong one).";
        StopForwarding(false);
        _passphrase = p;
        _cfg.PassphraseProtected = p.Length > 0 ? Pairing.Protect(p) : "";
        _cfg.Save();
        _lastError = null;
        if (p.Length == 0)
        {
            _deriving = false;
            _link.SetKey(null);
            UpdateUi();
            return null;
        }
        await UseKey(p);
        return null;
    }

    void OnIssue(LinkIssue issue)
    {
        UpdateUi();
        switch (issue)
        {
            case LinkIssue.Mismatch:
                Balloon("Passphrase mismatch", "Passphrase does not match (or the other PC is not ShareKM).", ToolTipIcon.Warning); break;
            case LinkIssue.OldPeer:
                Balloon("Update needed", "The other PC runs an older ShareKM. Update both PCs.", ToolTipIcon.Warning); break;
            case LinkIssue.NewerPeer:
                Balloon("Update needed", "The other PC runs a newer ShareKM. Update this PC.", ToolTipIcon.Warning); break;
        }
    }

    public string ComputerName => Environment.MachineName;
    public IReadOnlyList<string> LocalIps => [.. Discovery.LocalAddresses().Select(a => a.Ip.ToString())];

    // ---- used by the settings window ----

    public Keys Hotkey => _hotkey;
    public bool Connected => _link.Connected;
    public bool Paused => _link.Paused;
    public bool Forwarding => _forwarding;
    public bool RemoteControlling => _remoteControlling;
    public string Peer => _cfg.Peer;
    public string? PeerEndpoint => _link.PeerEndpoint;
    public IReadOnlyList<Discovery.Peer> Peers => _discovery.Peers;
    public string Status => StatusText();

    public int MouseRateHz => _motion.RateHz;

    // Applies at once (the sender reads the rate every period); the file write is debounced while a slider drags.
    public void SetMouseRate(int hz)
    {
        _motion.RateHz = hz;
        _cfg.MouseRateHz = _motion.RateHz;
        _saveLater.Stop();
        _saveLater.Start();
    }

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
        _motion.Start();
        _link.SendForwarding(true);
        UpdateUi();
        Balloon("Controlling the other computer", $"Press {KeyName(_hotkey)} on either keyboard to come back (or hold Esc 3 s).");
    }

    void StopForwarding(bool notify)
    {
        if (!_forwarding) return;
        _motion.Stop(); // sends any pending movement before the releases below
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
        if (up) Balloon("Connected (encrypted)", $"Linked with {PeerLabel}. Press {KeyName(_hotkey)} to switch.");
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
                    _motion.Flush();
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
                if (dx != 0 || dy != 0) _motion.Add(dx, dy);
                Native.SetCursorPos(_lock.x, _lock.y);
                break;
            case Native.WM_MOUSEWHEEL:
            case Native.WM_MOUSEHWHEEL:
                _motion.Flush();
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
                _motion.Flush();
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

    public string PeerLabel
    {
        get
        {
            var ip = _link.PeerEndpoint;
            var name = _discovery.Peers.FirstOrDefault(p => p.Address.ToString() == ip)?.Name;
            return name == null ? ip ?? "?" : $"{name} ({ip})";
        }
    }

    string StatusText() => Security switch
    {
        SecurityState.NotPaired => "Set a passphrase to connect",
        _ when _link.Paused => "Disconnected",
        SecurityState.Applying => "Applying the passphrase",
        SecurityState.Mismatch => "Passphrase mismatch",
        SecurityState.OldPeer or SecurityState.NewerPeer => "Update ShareKM on both PCs",
        _ when !_link.Connected => "Not connected",
        _ when _forwarding => $"Controlling {PeerLabel}, encrypted",
        _ when _remoteControlling => $"Controlled by {PeerLabel}, encrypted",
        _ => $"Connected to {PeerLabel}, encrypted",
    };

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
        var tip = (Elevation.IsElevated ? "ShareKM (administrator): " : "ShareKM: ") + status;
        _autostart.Checked = _autostartOn;
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

    public void Quit()
    {
        StopForwarding(false);
        if (_remoteControlling) _link.SendRelease();
        _watchdog.Stop();
        if (_saveLater.Enabled) _cfg.Save();
        _link.SetPaused(true); // closes the listener and the link before the next copy starts
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
