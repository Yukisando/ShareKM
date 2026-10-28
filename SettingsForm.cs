namespace ShareKM;

// What the settings window needs from the app (TrayApp implements it).
interface ISettingsHost
{
    Keys Hotkey { get; }
    bool Connected { get; }
    bool Paused { get; }
    bool Forwarding { get; }
    bool RemoteControlling { get; }
    string Peer { get; }
    string? PeerEndpoint { get; }
    string PeerLabel { get; }
    string Status { get; }
    IReadOnlyList<Discovery.Peer> Peers { get; }
    int MouseRateHz { get; }
    string ComputerName { get; }
    IReadOnlyList<string> LocalIps { get; }
    string Passphrase { get; }
    SecurityState Security { get; }
    Task<string?> ApplyPassphrase(string text);
    void SetMouseRate(int hz);
    bool IsElevated { get; }
    bool RunAsAdmin { get; }
    bool StartWithWindows { get; }
    bool Relaunching { get; }
    Task SetRunAsAdmin(bool on);
    Task RestartElevated();
    Task SetStartWithWindows(bool on);
    void SetHotkey(Keys key);
    void BeginRecord(Action<Keys?> done);
    void CancelRecord();
    void ConnectTo(string address);
    void Disconnect();
    void Resume();
    string AppVersion { get; }
    UpdateInfo Updates { get; }
    Task CheckForUpdates(bool quiet = false);
    Task InstallUpdate();
}

sealed class SettingsForm : Form
{
    public sealed record PeerItem(string Id, string Name, string Ip, bool Connected);

    const int CardWidth = 400, Inner = CardWidth - 32;

    readonly ISettingsHost _app;
    readonly Pill _status = new(), _hotkey = new() { KeyCap = true }, _secStatus = new();
    readonly Label _connHint, _rateValue, _secHint, _updateHint;
    readonly ModernButton _pause, _connect, _record, _copy, _show, _apply, _updateButton;
    readonly InputBox _pass = new();
    readonly Card _security = new();
    readonly ToggleSwitch _admin = new() { AccessibleName = "Run as administrator" }, _startup = new() { AccessibleName = "Start with Windows" };
    readonly Pill _adminStatus = new();
    readonly Label _startupHint;
    readonly ModernButton _elevate;
    readonly PeerList _peers = new() { EmptyText = "Looking for other computers running ShareKM..." };
    readonly InputBox _address = new();
    readonly ModernSlider _rate = new() { Minimum = MotionPump.MinHz, Maximum = MotionPump.MaxHz, Step = 10, AccessibleName = "Mouse update rate" };
    readonly Panel _scroller = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TableLayoutPanel _root;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly string _localIp;
    bool _recording, _applying, _confirming, _loaded;
    string? _applyError;

    public SettingsForm(ISettingsHost app)
    {
        _app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f / Theme.RenderScale, 96f / Theme.RenderScale);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ShareKM";
        Icon = TrayApp.AppIcon();
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;

        _root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(24, 16, 24, 24), Location = Point.Empty };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = Padding.Empty, Dock = DockStyle.Fill };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var headerText = Column();
        headerText.Controls.Add(Lbl("ShareKM", Theme.Title, false, 0, new Padding(0, 0, 0, 2)));
        headerText.Controls.Add(Lbl("Share one keyboard and mouse between your PCs.", Theme.Body, true, 0, new Padding(0, 0, 0, 0)));
        var credit = new CreditBadge { Url = "https://coldsnap.fr", Font = Theme.Caption, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(16, 6, 0, 0) };
        header.Controls.Add(headerText, 0, 0);
        header.Controls.Add(credit, 1, 0);
        _root.Controls.Add(header);
        _root.SetColumnSpan(header, 2);

        // ---- left column: the link ----
        var left = Column();
        left.Margin = new Padding(0, 0, 16, 0);

        _pause = MakeButton("", false, (_, _) => { if (_app.Paused) _app.Resume(); else _app.Disconnect(); RefreshState(); });
        var conn = new Card();
        _status.Font = Theme.Body;
        _status.Anchor = AnchorStyles.Left;
        conn.Controls.Add(_status, 0, 0);
        conn.Controls.Add(_pause, 1, 0);
        _connHint = Lbl("", Theme.Caption, true, Inner, new Padding(0, 8, 0, 0));
        Span(conn, _connHint, 1);
        Section(left, "Connection", conn, first: true);

        var me = new Card();
        var ips = _app.LocalIps;
        _localIp = ips.FirstOrDefault() ?? "";
        var meText = Column();
        meText.Anchor = AnchorStyles.Left;
        meText.Controls.Add(Lbl(_app.ComputerName, Theme.BodyStrong, false, Inner - 140, Padding.Empty));
        meText.Controls.Add(Lbl(ips.Count == 0 ? "No network connection" : string.Join(", ", ips), Theme.Caption, true, Inner - 140, Padding.Empty));
        _copy = MakeButton("C&opy address", false, (_, _) => CopyLocal());
        me.Controls.Add(meText, 0, 0);
        me.Controls.Add(_copy, 1, 0);
        Section(left, "This computer", me);

        var others = new Card();
        _peers.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _peers.Margin = new Padding(0, 0, 0, 8);
        Span(others, _peers, 0);
        _peers.List.SelectedIndexChanged += (_, _) => { if (_peers.List.SelectedItem is PeerItem p) _address.Box.Text = p.Ip; };
        _peers.List.DoubleClick += (_, _) => { if (_peers.List.SelectedItem is PeerItem) Connect(); };
        _address.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _address.Box.Text = _app.Peer;
        _address.Box.PlaceholderText = "IP address or computer name";
        _address.Box.AccessibleName = "Other computer's address";
        _connect = MakeButton("&Connect", true, (_, _) => Connect());
        others.Controls.Add(_address, 0, 1);
        others.Controls.Add(_connect, 1, 1);
        Span(others, Lbl("Pick a computer above or type its address. Connecting from either PC is enough, and it is remembered.",
            Theme.Caption, true, Inner, new Padding(0, 8, 0, 0)), 2);
        Section(left, "Other computers", others);

        // ---- right column: pairing and preferences ----
        var right = Column();

        // Security: the shared passphrase. Typed or generated here, applied with Apply (re-keys the link).
        _pass.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _pass.Box.UseSystemPasswordChar = true;
        _pass.Box.Text = _app.Passphrase;
        _pass.Box.PlaceholderText = "Passphrase";
        _pass.Box.AccessibleName = "Shared passphrase";
        _pass.Box.TextChanged += (_, _) => { _applyError = null; RefreshState(); };
        _pass.Box.Enter += (_, _) => AcceptButton = _apply;
        _pass.Box.Leave += (_, _) => AcceptButton = _connect;
        _show = MakeButton("S&how", false, (_, _) => { _pass.Box.UseSystemPasswordChar = !_pass.Box.UseSystemPasswordChar; _show!.Text = _pass.Box.UseSystemPasswordChar ? "S&how" : "&Hide"; });
        _security.Controls.Add(_pass, 0, 0);
        _security.Controls.Add(_show, 1, 0);
        var secButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 8, 0, 0), Anchor = AnchorStyles.Left };
        var generate = MakeButton("&Generate", false, (_, _) => { _pass.Box.Text = Pairing.Generate(); _pass.Box.Focus(); });
        var copyPass = MakeButton("Co&py", false, (_, _) => { try { if (_pass.Box.Text.Length > 0) Clipboard.SetText(_pass.Box.Text); } catch { } });
        generate.Margin = new Padding(0, 0, 8, 0);
        copyPass.Margin = Padding.Empty;
        secButtons.Controls.Add(generate);
        secButtons.Controls.Add(copyPass);
        _apply = MakeButton("&Apply", true, (_, _) => Apply());
        _apply.Margin = new Padding(8, 8, 0, 0);
        _security.Controls.Add(secButtons, 0, 1);
        _security.Controls.Add(_apply, 1, 1);
        Span(_security, Lbl("Use the same passphrase on both PCs.", Theme.Caption, true, Inner, new Padding(0, 8, 0, 12)), 2);
        _secStatus.Font = Theme.Body;
        _secStatus.Anchor = AnchorStyles.Left;
        Span(_security, _secStatus, 3);
        _secHint = Lbl("", Theme.Caption, true, Inner, new Padding(0, 8, 0, 0));
        Span(_security, _secHint, 4);
        Section(right, "Security", _security, first: true);


        var controls = new Card();
        var hkLabel = Lbl("Switch hotkey", Theme.Body, false, 0, Padding.Empty);
        hkLabel.Anchor = AnchorStyles.Left;
        var hkRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
        _hotkey.Font = Theme.BodyStrong;
        _hotkey.MinimumSize = new Size(72, 30);
        _hotkey.Anchor = AnchorStyles.Left;
        _hotkey.Margin = new Padding(0, 1, 0, 1);
        _record = MakeButton("&Record", false, (_, _) => ToggleRecord());
        hkRow.Controls.Add(_hotkey);
        hkRow.Controls.Add(_record);
        controls.Controls.Add(hkLabel, 0, 0);
        controls.Controls.Add(hkRow, 1, 0);
        Span(controls, Lbl("Press it on either keyboard to move control between the PCs. Holding Esc for 3 seconds always brings control back.",
            Theme.Caption, true, Inner, new Padding(0, 8, 0, 0)), 1);
        // Run as administrator: Windows keeps normal programs away from elevated windows (UIPI), on both PCs.
        var adminTitle = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 16, 0, 0), Anchor = AnchorStyles.Left };
        var shield = new PictureBox
        {
            Image = SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(16, 16), Margin = new Padding(0, 3, 8, 0),
        };
        var adminLabel = Lbl("Run as administrator", Theme.Body, false, 0, Padding.Empty);
        adminTitle.Controls.Add(shield);
        adminTitle.Controls.Add(adminLabel);
        _admin.Anchor = AnchorStyles.Right;
        _admin.Margin = new Padding(8, 16, 0, 0);
        _admin.CheckedChanged += async (_, _) => await AdminToggled();
        controls.Controls.Add(adminTitle, 0, 2);
        controls.Controls.Add(_admin, 1, 2);
        Span(controls, Lbl("Lets ShareKM control administrator windows (admin terminals, Task Manager). Windows asks once. " +
            "Turn it on on both PCs. The UAC prompt, lock screen and Ctrl+Alt+Del stay out of reach.",
            Theme.Caption, true, Inner, new Padding(0, 8, 0, 8)), 3);
        _adminStatus.Font = Theme.Caption;
        _adminStatus.MinimumSize = new Size(0, 24);
        _adminStatus.Anchor = AnchorStyles.Left;
        _elevate = MakeButton("Restart as a&dministrator", false, async (_, _) => await _app.RestartElevated());
        _elevate.Anchor = AnchorStyles.Left;
        _elevate.Margin = new Padding(12, 0, 0, 0);
        _adminStatus.Margin = new Padding(0, 4, 0, 4);
        var adminRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(Inner, 0) };
        adminRow.Controls.Add(_adminStatus);
        adminRow.Controls.Add(_elevate);
        Span(controls, adminRow, 4);

        var startupLabel = Lbl("Start with Windows", Theme.Body, false, 0, new Padding(0, 16, 0, 0));
        startupLabel.Anchor = AnchorStyles.Left;
        _startup.Anchor = AnchorStyles.Right;
        _startup.Margin = new Padding(8, 16, 0, 0);
        _startup.CheckedChanged += async (_, _) => { await _app.SetStartWithWindows(_startup.Checked); RefreshState(); };
        controls.Controls.Add(startupLabel, 0, 5);
        controls.Controls.Add(_startup, 1, 5);
        _startupHint = Lbl("", Theme.Caption, true, Inner, new Padding(0, 8, 0, 0));
        Span(controls, _startupHint, 6);
        Section(right, "Controls", controls);

        var updates = new Card();
        var versionLabel = Lbl($"Version {_app.AppVersion}", Theme.Body, false, 0, Padding.Empty);
        versionLabel.Anchor = AnchorStyles.Left;
        _updateButton = MakeButton("Check for &updates", false, async (_, _) =>
        {
            if (_app.Updates.Available != null) await _app.InstallUpdate(); else await _app.CheckForUpdates();
        });
        updates.Controls.Add(versionLabel, 0, 0);
        updates.Controls.Add(_updateButton, 1, 0);
        _updateHint = Lbl("", Theme.Caption, true, Inner, new Padding(0, 8, 0, 0));
        Span(updates, _updateHint, 1);
        Section(right, "Updates", updates);

        var perf = new Card();
        var rateLabel = Lbl("Mouse update rate", Theme.Body, false, 0, Padding.Empty);
        rateLabel.Anchor = AnchorStyles.Left;
        _rateValue = Lbl("", Theme.BodyStrong, false, 0, Padding.Empty);
        _rateValue.Anchor = AnchorStyles.Right;
        perf.Controls.Add(rateLabel, 0, 0);
        perf.Controls.Add(_rateValue, 1, 0);
        _rate.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _rate.Margin = new Padding(0, 4, 0, 0);
        _rate.Value = _app.MouseRateHz;
        _rate.ValueChanged += (_, _) => { _app.SetMouseRate(_rate.Value); _rateValue.Text = $"{_rate.Value} Hz"; };
        Span(perf, _rate, 1);
        var rateHint = Lbl("Higher = smoother, uses more Wi-Fi and CPU. Only matters on the PC you are controlling from.",
            Theme.Caption, true, Inner - 96, new Padding(0, 4, 0, 0));
        var reset = new LinkLabel { Text = "Reset to 200", AutoSize = true, Font = Theme.Caption, Margin = new Padding(8, 4, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Right, LinkBehavior = LinkBehavior.HoverUnderline, TabStop = true };
        reset.LinkClicked += (_, _) => _rate.Value = MotionPump.DefaultHz;
        perf.Controls.Add(rateHint, 0, 2);
        perf.Controls.Add(reset, 1, 2);
        Section(left, "Performance", perf);

        _root.Controls.Add(left, 0, 1);
        _root.Controls.Add(right, 1, 1);
        _root.SizeChanged += (_, _) => { if (_loaded) FitToContent(); }; // texts that grow or shrink later
        _scroller.Controls.Add(_root);
        Controls.Add(_scroller);
        AcceptButton = _connect;
        ResumeLayout(false);

        _timer.Tick += (_, _) => RefreshState();
        Theme.Changed += OnThemeChanged;
        FormClosed += (_, _) => { Theme.Changed -= OnThemeChanged; _timer.Dispose(); if (_recording) _app.CancelRecord(); };
        ApplyTheme();
        RefreshState();
    }

    // ---- building blocks ----

    static TableLayoutPanel Column()
    {
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Left };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    static void Section(TableLayoutPanel column, string title, Card card, bool first = false)
    {
        column.Controls.Add(Lbl(title, Theme.Subtitle, false, 0, new Padding(2, first ? 16 : 20, 0, 8)));
        card.MinimumSize = new Size(CardWidth, 0);
        card.MaximumSize = new Size(CardWidth, 0);
        card.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        column.Controls.Add(card);
    }

    static void Span(Card card, Control c, int row)
    {
        card.Controls.Add(c, 0, row);
        card.SetColumnSpan(c, 2);
    }

    static Label Lbl(string text, Font font, bool secondary, int maxWidth, Padding margin) => new()
    {
        Text = text, Font = font, AutoSize = true, Margin = margin, Tag = secondary ? "secondary" : null, UseMnemonic = false,
        MaximumSize = new Size(maxWidth, 0),
    };

    ModernButton MakeButton(string text, bool primary, EventHandler click)
    {
        var b = new ModernButton { Text = text, Primary = primary, Font = Theme.Body, Anchor = AnchorStyles.Right };
        b.Click += click;
        return b;
    }

    // ---- theme ----

    void OnThemeChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(ApplyTheme);
    }

    void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = _scroller.BackColor = _root.BackColor = p.Window;
        PaintTree(_root, p.Window);
        _address.ApplyTheme();
        _pass.ApplyTheme();
        _peers.ApplyTheme();
        if (_scroller.IsHandleCreated) Native.SetWindowTheme(_scroller.Handle, p.Dark ? "DarkMode_Explorer" : "Explorer", null);
        Theme.ApplyChrome(this);
        Invalidate(true);
    }

    static void PaintTree(Control parent, Color back)
    {
        var p = Theme.Current;
        foreach (Control c in parent.Controls)
        {
            var bg = back;
            switch (c)
            {
                case Card: bg = p.Card; c.BackColor = back; break;
                case LinkLabel l: l.BackColor = back; l.LinkColor = l.ActiveLinkColor = p.Accent; l.VisitedLinkColor = p.Accent; break;
                case Label l: l.BackColor = back; l.ForeColor = (string?)l.Tag == "secondary" ? p.TextSecondary : p.Text; break;
                case TableLayoutPanel or FlowLayoutPanel: c.BackColor = back; break;
                default: c.BackColor = back; break;
            }
            PaintTree(c, bg);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _loaded = true;
        FitToContent();
        _timer.Start();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(() => { ApplyTheme(); FitToContent(); });
    }

    // Sized to the content, so it only scrolls (vertically) when the screen is too short for it. Runs again whenever
    // the content changes size, and keeps the window on screen.
    void FitToContent()
    {
        var pref = _root.GetPreferredSize(Size.Empty);
        var area = Screen.FromControl(this).WorkingArea;
        int maxH = area.Height - (Height - ClientSize.Height) - 16;
        bool scroll = pref.Height > maxH;
        int w = pref.Width + (scroll ? SystemInformation.VerticalScrollBarWidth : 0);
        _scroller.AutoScroll = scroll;
        var size = new Size(Math.Min(w, area.Width - 16), Math.Min(pref.Height, maxH));
        if (ClientSize == size) return;
        ClientSize = size;
        if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
        if (Right > area.Right) Left = Math.Max(area.Left, area.Right - Width);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && !_recording) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- behaviour ----

    void ToggleRecord()
    {
        if (_recording)
        {
            _app.CancelRecord();
            EndRecord();
            return;
        }
        _recording = true;
        _record.Text = "&Cancel";
        _hotkey.Text = "Press a key...";
        _app.BeginRecord(key =>
        {
            if (IsDisposed) return;
            if (key is Keys k) _app.SetHotkey(k);
            EndRecord();
        });
    }

    void EndRecord()
    {
        _recording = false;
        _record.Text = "&Record";
        RefreshState();
    }

    async void CopyLocal()
    {
        try { Clipboard.SetText(_localIp.Length > 0 ? _localIp : Environment.MachineName); } catch { return; }
        _copy.Text = "Copied";
        await Task.Delay(1500);
        if (!IsDisposed) _copy.Text = "C&opy address";
    }

    // Switching on asks first (every time); Cancel puts the switch back with no other effect.
    async Task AdminToggled()
    {
        bool on = _admin.Checked;
        if (on == _app.RunAsAdmin) return;
        _confirming = true;
        bool confirmed = !on || AdminWarningDialog.Confirm(this);
        _confirming = false;
        if (!confirmed)
        {
            _admin.SetSilently(false);
            return;
        }
        _admin.Enabled = false;
        await _app.SetRunAsAdmin(on); // may restart ShareKM; this window then closes with the old copy
        if (IsDisposed) return;
        _admin.Enabled = true;
        RefreshState();
    }

    async void Apply()
    {
        _applying = true;
        RefreshState();
        var error = await _app.ApplyPassphrase(_pass.Box.Text);
        _applying = false;
        if (IsDisposed) return;
        if (error == null) _pass.Box.Text = _app.Passphrase;
        _applyError = error;
        RefreshState();
    }

    void Connect()
    {
        if (_address.Box.Text.Trim().Length == 0) { _address.Box.Focus(); return; }
        _app.ConnectTo(_address.Box.Text);
        RefreshState();
    }

    public void RefreshState()
    {
        if (IsDisposed) return;
        var key = TrayApp.KeyName(_app.Hotkey);
        if (!_recording) _hotkey.Text = key;
        var sec = _app.Security;
        var green = Color.FromArgb(0x2E, 0xA8, 0x4A);
        string? problem = sec switch
        {
            SecurityState.NotPaired => "Not paired",
            SecurityState.Applying => "Applying passphrase",
            SecurityState.Mismatch => "Passphrase mismatch",
            SecurityState.OldPeer or SecurityState.NewerPeer => "Update needed",
            _ => null,
        };
        _status.Text = _app.Paused ? "Disconnected" : problem ?? (!_app.Connected ? "Not connected"
            : _app.Forwarding ? "Controlling the other PC" : _app.RemoteControlling ? "Being controlled" : "Connected");
        _status.Dot = sec is SecurityState.Mismatch or SecurityState.OldPeer or SecurityState.NewerPeer ? Color.DarkOrange
            : _app.Paused || !_app.Connected ? Color.Gray
            : _app.Forwarding ? Color.DarkOrange
            : _app.RemoteControlling ? Color.DodgerBlue
            : green;

        var (secText, secDot, secHint) = sec switch
        {
            SecurityState.NotPaired => ("Not paired", Color.Gray, "Set the same passphrase on both PCs, then press Apply."),
            SecurityState.Applying => ("Applying...", Color.Gray, "Preparing the key."),
            SecurityState.Encrypted => ("Encrypted (AES-256-GCM)", green, "Only PCs with this passphrase can connect."),
            SecurityState.Mismatch => ("Passphrase mismatch", Color.DarkOrange, "Passphrase does not match (or the other PC is not ShareKM)."),
            SecurityState.OldPeer => ("Update needed", Color.DarkOrange, "The other PC runs an older ShareKM. Update both PCs."),
            SecurityState.NewerPeer => ("Update needed", Color.DarkOrange, "The other PC runs a newer ShareKM. Update this PC."),
            _ => ("Ready", Color.Gray, "Waiting for the other computer. It needs the same passphrase."),
        };
        bool dirty = Pairing.Normalize(_pass.Box.Text) != _app.Passphrase;
        _secStatus.Text = secText;
        _secStatus.Dot = secDot;
        _secHint.Text = _applyError ?? (dirty && !_applying ? (_pass.Box.Text.Length == 0 ? "Press Apply to remove the passphrase (this PC stops connecting)." : "Press Apply to use this passphrase.") : secHint);
        _apply.Enabled = dirty && !_applying;
        if (_security.Highlight != (sec == SecurityState.NotPaired)) { _security.Highlight = sec == SecurityState.NotPaired; _security.Invalidate(); }
        _pause.Text = _app.Paused ? "Co&nnect" : "&Disconnect";
        _pause.Primary = _app.Paused;
        _pause.Invalidate();
        _connHint.Text = sec == SecurityState.NotPaired ? "Set a passphrase under Security to connect."
            : problem != null && sec != SecurityState.Applying ? secHint
            : _app.Paused ? "Disconnected. Press Connect to allow connections again."
            : !_app.Connected ? "Waiting for the other computer. Pick it below, or start ShareKM on it."
            : _app.Forwarding ? $"Your keyboard and mouse control {_app.PeerLabel}. Press {key} to come back."
            : _app.RemoteControlling ? $"{_app.PeerLabel} is controlling this PC. Press {key} here to give control back."
            : $"Linked with {_app.PeerLabel}, encrypted. Press {key} to control it.";
        _rateValue.Text = $"{_rate.Value} Hz";

        bool pending = _app.RunAsAdmin && !_app.IsElevated;
        if (!_confirming)
        {
            if (_admin.Enabled) _admin.SetSilently(_app.RunAsAdmin);
            _admin.Enabled = !_app.Relaunching;
        }
        (_adminStatus.Text, _adminStatus.Dot) = _app.Relaunching ? ("Restarting ShareKM...", Color.Gray)
            : _app.IsElevated ? ("Running as administrator", Color.DodgerBlue)
            : pending ? ("Not running as administrator yet", Color.DarkOrange)
            : ("Not running as administrator", Color.Gray);
        _elevate.Visible = pending && !_app.Relaunching;
        _startup.SetSilently(_app.StartWithWindows);
        _startupHint.Text = !_app.StartWithWindows ? "ShareKM is not started when you sign in."
            : _app.RunAsAdmin && _app.IsElevated ? "Starts as administrator when you sign in (scheduled task, no prompt)."
            : pending ? "Starts when you sign in, without administrator rights until you restart it as administrator."
            : "Starts when you sign in.";

        var up = _app.Updates;
        _updateButton.Text = up.Installing ? $"Updating... {up.Percent}%" : up.Checking ? "Checking..."
            : up.Available is { } r ? $"&Update to {r.Version}" : "Check for &updates";
        _updateButton.Primary = up.Available != null;
        _updateButton.Enabled = !up.Installing && !up.Checking && !_app.Relaunching;
        _updateButton.Invalidate();
        _updateHint.Text = up.Error ?? (up.Installing ? "Downloading from GitHub. ShareKM restarts by itself."
            : up.Available is { } a ? $"Version {a.Version} is out. ShareKM downloads it and restarts. Update both PCs."
            : up.UpToDate ? "You have the latest version."
            : "Updates come from the ShareKM releases on GitHub.");

        var current = _app.Connected ? _app.PeerEndpoint : null;
        var items = _app.Peers.Select(p => new PeerItem(p.Id, p.Name, p.Address.ToString(), p.Address.ToString() == current)).ToList();
        _peers.SetItems(items, (_peers.List.SelectedItem as PeerItem)?.Id);
    }
}
