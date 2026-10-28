namespace ShareKM;

sealed class SettingsForm : Form
{
    readonly TrayApp _app;
    readonly Label _hotkey, _status;
    readonly Button _record, _pause;
    readonly ListBox _peers;
    readonly TextBox _address;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly string _localIp;

    public SettingsForm(TrayApp app)
    {
        _app = app;
        Text = "ShareKM";
        Icon = TrayApp.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        ShowInTaskbar = true;

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _hotkey = Value(new Font(Font, FontStyle.Bold));
        _record = Btn("Record", (_, _) => Record());
        Row(grid, "Switch hotkey:", _hotkey, _record);

        var ips = Discovery.LocalAddresses().Select(a => a.Ip.ToString()).ToList();
        _localIp = ips.FirstOrDefault() ?? "";
        var me = Value();
        me.Text = $"{Environment.MachineName}   {(ips.Count == 0 ? "(no network)" : string.Join(", ", ips))}";
        Row(grid, "This computer:", me, Btn("Copy", (_, _) => CopyLocal()));

        _status = Value();
        _pause = Btn("Disconnect", (_, _) => { if (_app.Paused) _app.Resume(); else _app.Disconnect(); });
        Row(grid, "Status:", _status, _pause);

        var found = new Label { Text = "Other computers running ShareKM on this network:", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        grid.Controls.Add(found);
        grid.SetColumnSpan(found, 3);
        _peers = new ListBox { Height = 96, Dock = DockStyle.Fill, IntegralHeight = false };
        _peers.SelectedIndexChanged += (_, _) => { if (_peers.SelectedItem is PeerItem { Ip.Length: > 0 } p) _address!.Text = p.Ip; };
        _peers.DoubleClick += (_, _) => { if (_peers.SelectedItem is PeerItem { Ip.Length: > 0 }) Connect(); };
        grid.Controls.Add(_peers);
        grid.SetColumnSpan(_peers, 3);

        _address = new TextBox { Text = _app.Peer, Dock = DockStyle.Fill, PlaceholderText = "IP address or computer name" };
        Row(grid, "Connect to:", _address, Btn("Connect", (_, _) => Connect()));

        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText,
            Text = "Pick a computer above or type its address. Connecting from either PC is enough; it is remembered.",
        };
        grid.Controls.Add(hint);
        grid.SetColumnSpan(hint, 3);

        var close = Btn("Close", (_, _) => Close());
        close.Anchor = AnchorStyles.Right;
        close.Margin = new Padding(3, 12, 3, 3);
        grid.Controls.Add(close);
        grid.SetColumnSpan(close, 3);

        Controls.Add(grid);
        CancelButton = close;
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
        RefreshState();
    }

    static Label Value(Font? font = null) =>
        new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = font, MaximumSize = new Size(260, 0) };

    static Button Btn(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(90, 0), Anchor = AnchorStyles.Left | AnchorStyles.Right };
        b.Click += click;
        return b;
    }

    static void Row(TableLayoutPanel grid, string caption, Control value, Control action)
    {
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(value);
        grid.Controls.Add(action);
    }

    void Record()
    {
        _record.Enabled = false;
        _hotkey.Text = "Press any key... (Esc cancels)";
        _app.BeginRecord(key =>
        {
            if (IsDisposed) return;
            if (key is Keys k) _app.SetHotkey(k);
            _record.Enabled = true;
            RefreshState();
        });
    }

    void CopyLocal()
    {
        try { Clipboard.SetText(_localIp.Length > 0 ? _localIp : Environment.MachineName); } catch { }
    }

    void Connect()
    {
        if (_address.Text.Trim().Length == 0) return;
        _app.ConnectTo(_address.Text);
        RefreshState();
    }

    public void RefreshState()
    {
        if (IsDisposed) return;
        if (_record.Enabled) _hotkey.Text = TrayApp.KeyName(_app.Hotkey);
        _status.Text = _app.Status;
        _pause.Text = _app.Paused ? "Connect" : "Disconnect";

        var current = _app.Connected ? _app.PeerEndpoint : null;
        var items = _app.Peers.Select(p => new PeerItem(p.Id, p.Address.ToString(),
            $"{p.Name}   {p.Address}{(p.Address.ToString() == current ? "   (connected)" : "")}")).ToList();
        if (items.Count == 0) items.Add(new PeerItem("", "", "(none found yet; the other PC must run ShareKM)"));
        if (items.SequenceEqual(_peers.Items.Cast<PeerItem>())) return;
        var selected = (_peers.SelectedItem as PeerItem)?.Id;
        _peers.BeginUpdate();
        _peers.Items.Clear();
        foreach (var i in items) _peers.Items.Add(i);
        int idx = items.FindIndex(i => i.Id.Length > 0 && i.Id == selected);
        if (idx >= 0) _peers.SelectedIndex = idx;
        _peers.EndUpdate();
    }

    sealed record PeerItem(string Id, string Ip, string Label)
    {
        public override string ToString() => Label;
    }
}
