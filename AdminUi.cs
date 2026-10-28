using System.ComponentModel;
using System.Media;

namespace ShareKM;

// Win11 toggle switch with an "On"/"Off" label on its left.
sealed class ToggleSwitch : Control
{
    bool _checked, _hover;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        Size = new Size(88, 32);
        MinimumSize = new Size(88, 32);
        Margin = new Padding(8, 0, 0, 0);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Sets the state without raising CheckedChanged (for showing the app's state).
    public void SetSilently(bool on)
    {
        if (_checked == on) return;
        _checked = on;
        Invalidate();
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessible(this);

    sealed class SwitchAccessible(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : 0);
        public override string DefaultAction => "Toggle";
        public override void DoDefaultAction() => owner.Checked = !owner.Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this);
        g.Clear(Ui.BackOf(this));
        float w = 40 * s, h = 20 * s, x = Width - w - 2 * s, y = (Height - h) / 2;
        var track = new RectangleF(x, y, w, h);
        var text = Enabled ? p.Text : p.TextDisabled;
        if (_checked && Enabled) Ui.FillRounded(g, _hover ? p.AccentHover : p.Accent, null, track, h / 2);
        else Ui.FillRounded(g, _hover ? p.ControlHover : p.Control, Enabled ? p.TextSecondary : p.TextDisabled, track, h / 2);
        float r = (_hover ? 7 : 6) * s, cx = _checked ? x + w - h / 2 : x + h / 2;
        using (var knob = new SolidBrush(_checked && Enabled ? p.OnAccent : text))
            g.FillEllipse(knob, cx - r, y + h / 2 - r, 2 * r, 2 * r);
        var label = new Rectangle(0, 0, (int)(x - 8 * s), Height);
        TextRenderer.DrawText(g, _checked ? "On" : "Off", Font, label, text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) Ui.FillRounded(g, Color.Transparent, p.Text, new RectangleF(0, 0, Width, Height), 4 * s, 2 * s);
    }
}

// The confirmation shown before administrator mode is turned on. Themed like the settings window
// (light/dark, accent), with the standard Windows warning icon and sound. Cancel is the default button.
sealed class AdminWarningDialog : Form
{
    public const string Heading = "Run ShareKM as administrator?";
    public const string Body =
        "ShareKM will be able to control administrator windows (admin terminals, Task Manager, installers).\n\n" +
        "Only PCs that know your passphrase can connect, but while ShareKM runs as administrator, anyone with " +
        "your passphrase, or any malware on a paired PC, could type into admin windows on this PC.\n\n" +
        "Use it only on a network you trust, and turn it off when you do not need it. Turn it on for both PCs for it to work.";

    public AdminWarningDialog()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f / Theme.RenderScale, 96f / Theme.RenderScale);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ShareKM";
        Icon = TrayApp.AppIcon();
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var p = Theme.Current;
        BackColor = p.Window;
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(24, 24, 24, 0), Margin = Padding.Empty, BackColor = p.Window };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = new PictureBox
        {
            Image = SystemIcons.GetStockIcon(StockIconId.Warning, StockIconOptions.Default).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(32, 32), Margin = new Padding(0, 2, 16, 0), BackColor = p.Window,
        };
        grid.Controls.Add(icon, 0, 0);
        grid.SetRowSpan(icon, 2);
        grid.Controls.Add(new Label { Text = Heading, Font = Theme.Font(14f, true, true), ForeColor = p.Text, BackColor = p.Window, AutoSize = true, Margin = new Padding(0, 0, 0, 12), UseMnemonic = false }, 1, 0);
        grid.Controls.Add(new Label { Text = Body, Font = Theme.Body, ForeColor = p.Text, BackColor = p.Window, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 0, 0, 24), UseMnemonic = false }, 1, 1);

        // Win11 dialogs put the buttons on a slightly different footer band.
        var footer = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(24, 16, 24, 16), Margin = Padding.Empty, BackColor = p.Card };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var turnOn = new ModernButton { Text = "&Turn on", Primary = true, Font = Theme.Body, DialogResult = DialogResult.OK, BackColor = p.Card, MinimumSize = new Size(120, 32) };
        var cancel = new ModernButton { Text = "Cancel", Font = Theme.Body, DialogResult = DialogResult.Cancel, BackColor = p.Card, MinimumSize = new Size(120, 32) };
        footer.Controls.Add(new Label { AutoSize = true, BackColor = p.Card }, 0, 0);
        footer.Controls.Add(turnOn, 1, 0);
        footer.Controls.Add(cancel, 2, 0);

        var outer = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = p.Window, Margin = Padding.Empty };
        outer.Controls.Add(grid, 0, 0);
        outer.Controls.Add(footer, 0, 1);
        footer.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(outer);
        AcceptButton = cancel;
        CancelButton = cancel;
        ActiveControl = cancel;
        ResumeLayout(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyChrome(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SystemSounds.Exclamation.Play();
    }

    public static bool Confirm(IWin32Window owner)
    {
        using var d = new AdminWarningDialog();
        return d.ShowDialog(owner) == DialogResult.OK;
    }
}
