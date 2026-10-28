using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ShareKM;

// Small owner-drawn controls in the Windows 11 style. Sizes are 96-dpi logical values; AutoScaleMode.Dpi scales
// sizes, margins and paddings, and painting scales radii and strokes with S().
static class Ui
{
    public static float S(Control c) => c.DeviceDpi / 96f * Theme.RenderScale;

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Color fill, Color? border, RectangleF r, float radius, float stroke = 1)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = border == null ? r : RectangleF.Inflate(r, -stroke / 2, -stroke / 2);
        using var path = Rounded(inset, radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is Color bc)
            using (var pen = new Pen(bc, stroke)) g.DrawPath(pen, path);
    }

    // Every control gets its surface colour (window or card) as BackColor from the settings form.
    public static Color BackOf(Control c) => c.BackColor.A == 255 ? c.BackColor : Theme.Current.Window;
}

// A section card: rounded, subtle border, lifted fill. It is a TableLayoutPanel so it auto-sizes reliably.
sealed class Card : TableLayoutPanel
{
    // Draws an accent border to point the user at this card (e.g. Security on first run).
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Highlight { get; set; }

    public Card()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(0, 0, 0, 0);
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.Current;
        e.Graphics.Clear(Ui.BackOf(this));
        float s = Ui.S(this);
        Ui.FillRounded(e.Graphics, p.Card, Highlight ? p.Accent : p.CardBorder, new RectangleF(0, 0, Width, Height), 8 * s, Highlight ? 2 * s : 1);
    }
}

sealed class ModernButton : Button
{
    bool _hover, _press;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Primary { get; set; }

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 0, 12, 0);
        MinimumSize = new Size(96, 32);
        Margin = new Padding(8, 0, 0, 0);
        UseMnemonic = true;
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(Math.Max(t.Width + Padding.Horizontal, MinimumSize.Width), Math.Max(t.Height + 8, MinimumSize.Height));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _press = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _press = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this);
        g.Clear(Ui.BackOf(this));
        var r = new RectangleF(0, 0, Width, Height);
        Color fill, text;
        Color? border;
        if (!Enabled) { fill = p.Control; border = p.ControlBorder; text = p.TextDisabled; }
        else if (Primary) { fill = _press ? p.AccentPressed : _hover ? p.AccentHover : p.Accent; border = null; text = p.OnAccent; }
        else { fill = _press ? p.ControlPressed : _hover ? p.ControlHover : p.Control; border = p.ControlBorder; text = _press ? p.TextSecondary : p.Text; }
        Ui.FillRounded(g, fill, border, r, 4 * s);
        if (Focused && ShowFocusCues)
            Ui.FillRounded(g, Color.Transparent, p.Text, r, 4 * s, 2 * s);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), text, fill, flags);
    }
}

// Win11-style slider: thin track, accent-filled part, round thumb with an accent centre.
sealed class ModernSlider : Control
{
    int _value, _min, _max = 100;
    bool _drag, _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Step { get; set; } = 1;
    public event EventHandler? ValueChanged;

    public ModernSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        Size = new Size(240, 32);
        MinimumSize = new Size(120, 32);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Minimum { get => _min; set { _min = value; Value = _value; } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Maximum { get => _max; set { _max = value; Value = _value; } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp((int)Math.Round(value / (double)Step) * Step, _min, _max);
            if (v == _value) return;
            _value = v;
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    float ThumbR => 10 * Ui.S(this);
    float TrackLeft => ThumbR + 2;
    float TrackRight => Width - ThumbR - 2;
    float XOf(int v) => TrackLeft + (TrackRight - TrackLeft) * (v - _min) / Math.Max(1, _max - _min);
    int ValueAt(float x) => _min + (int)Math.Round((x - TrackLeft) / Math.Max(1, TrackRight - TrackLeft) * (_max - _min));

    protected override bool IsInputKey(Keys k) =>
        (k & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int big = Step * 10;
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= Step; break;
            case Keys.Right or Keys.Up: Value += Step; break;
            case Keys.PageDown: Value -= big; break;
            case Keys.PageUp: Value += big; break;
            case Keys.Home: Value = _min; break;
            case Keys.End: Value = _max; break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _drag = true;
        Capture = true;
        Value = ValueAt(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) Value = ValueAt(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Capture = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += Math.Sign(e.Delta) * Step; }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessible(this);

    sealed class SliderAccessible(ModernSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.Value.ToString(); set { if (int.TryParse(value, out var v)) owner.Value = v; } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this), cy = Height / 2f, th = 4 * s, x = XOf(_value);
        g.Clear(Ui.BackOf(this));
        Ui.FillRounded(g, p.ControlStrong, null, new RectangleF(TrackLeft, cy - th / 2, TrackRight - TrackLeft, th), th / 2);
        Ui.FillRounded(g, p.Accent, null, new RectangleF(TrackLeft, cy - th / 2, x - TrackLeft, th), th / 2);
        float r = ThumbR;
        using (var outer = new SolidBrush(p.Dark ? C(0x454545) : Color.White))
            g.FillEllipse(outer, x - r, cy - r, 2 * r, 2 * r);
        using (var ring = new Pen(p.ControlBorder, 1))
            g.DrawEllipse(ring, x - r, cy - r, 2 * r, 2 * r);
        float inner = (_drag ? 0.45f : _hover || Focused ? 0.7f : 0.55f) * r;
        using (var dot = new SolidBrush(p.Accent))
            g.FillEllipse(dot, x - inner, cy - inner, 2 * inner, 2 * inner);
        if (Focused && ShowFocusCues)
            Ui.FillRounded(g, Color.Transparent, p.Text, new RectangleF(0, 0, Width, Height), 4 * s, 2 * s);
    }

    static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

// Rounded label: a status badge with a coloured dot, or a key cap.
sealed class Pill : Control
{
    Color? _dot;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool KeyCap { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Dot { get => _dot; set { _dot = value; Invalidate(); } }

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        Padding = new Padding(10, 0, 12, 0);
        MinimumSize = new Size(0, 28);
        Margin = new Padding(0);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        int dot = _dot != null || !KeyCap ? (int)(16 * Ui.S(this)) : 0;
        return new Size(t.Width + Padding.Horizontal + dot, Math.Max(t.Height + 8, MinimumSize.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this);
        g.Clear(Ui.BackOf(this));
        var r = new RectangleF(0, 0, Width, Height);
        Color fill = KeyCap ? p.Selection : Theme.Blend(_dot ?? p.TextSecondary, Ui.BackOf(this), p.Dark ? 0.22f : 0.14f);
        if (KeyCap)
        {
            Ui.FillRounded(g, p.ControlStrong, null, r, 5 * s);                          // key cap: darker bottom edge
            Ui.FillRounded(g, fill, null, new RectangleF(s, s, Width - 2 * s, Height - 4 * s), 4 * s);
        }
        else Ui.FillRounded(g, fill, null, r, Height / 2f);
        int x = Padding.Left;
        if (!KeyCap)
        {
            float d = 8 * s, cy = (Height - (KeyCap ? 2 * s : 0)) / 2f;
            using var b = new SolidBrush(_dot ?? p.TextSecondary);
            g.FillEllipse(b, x, cy - d / 2, d, d);
            x += (int)(16 * s);
        }
        var tr = new Rectangle(x, 0, Width - x - Padding.Right + 4, (int)(Height - (KeyCap ? 2 * s : 0)));
        TextRenderer.DrawText(g, Text, Font, tr, p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (KeyCap ? TextFormatFlags.HorizontalCenter : 0));
    }
}

// Win11 text box: rounded field, accent underline when focused.
sealed class InputBox : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };

    public InputBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(10, 0, 10, 0);
        Size = new Size(200, 32);
        MinimumSize = new Size(0, 32);
        Margin = new Padding(0);
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => Box.Focus();
    }

    public override Size GetPreferredSize(Size proposed) =>
        new(Math.Max(Width, 120), Math.Max(Box.PreferredHeight + 12, MinimumSize.Height));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Box.PreferredHeight;
        Box.SetBounds(Padding.Left, (Height - h) / 2, Width - Padding.Horizontal, h);
    }

    public void ApplyTheme()
    {
        var p = Theme.Current;
        Box.BackColor = p.Control;
        Box.ForeColor = p.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this);
        g.Clear(Ui.BackOf(this));
        var r = new RectangleF(0, 0, Width, Height);
        Ui.FillRounded(g, p.Control, p.ControlBorder, r, 4 * s);
        bool focus = Box.Focused;
        float line = focus ? 2 * s : 1;
        using var clip = Ui.Rounded(r, 4 * s);
        g.SetClip(clip);
        using (var b = new SolidBrush(focus ? p.Accent : p.ControlStrong))
            g.FillRectangle(b, 0, Height - line, Width, line);
        g.ResetClip();
    }
}

// Owner-drawn list of discovered computers inside a rounded frame, with an empty-state message.
sealed class PeerList : Panel
{
    public readonly ListBox List = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None, IntegralHeight = false,
    };
    readonly Label _empty = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string EmptyText { get => _empty.Text; set => _empty.Text = value; }

    public PeerList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(4);
        Margin = new Padding(0);
        Size = new Size(200, 132);
        MinimumSize = new Size(0, 132);
        Controls.Add(_empty);
        Controls.Add(List);
        List.Dock = DockStyle.Fill;
        List.DrawItem += DrawPeer;
        List.Enter += (_, _) => Invalidate();
        List.Leave += (_, _) => Invalidate();
        List.HandleCreated += (_, _) => ApplyTheme();
        List.SizeChanged += (_, _) => Reinsert();
        _empty.Click += (_, _) => List.Focus();
    }

    // The native list keeps a scrollbar computed for its old size or item height; re-adding the few items resets it.
    void Reinsert()
    {
        if (!List.IsHandleCreated || List.Items.Count == 0) return;
        var items = List.Items.Cast<object>().ToArray();
        int sel = List.SelectedIndex;
        List.BeginUpdate();
        List.Items.Clear();
        List.Items.AddRange(items);
        List.SelectedIndex = sel < items.Length ? sel : -1;
        List.EndUpdate();
    }

    public void SetItems(IReadOnlyList<SettingsForm.PeerItem> items, string? selectedId)
    {
        if (items.SequenceEqual(List.Items.Cast<SettingsForm.PeerItem>())) return;
        _empty.Visible = items.Count == 0;
        List.Visible = items.Count > 0;
        List.BeginUpdate();
        List.Items.Clear();
        foreach (var i in items) List.Items.Add(i);
        int idx = selectedId == null ? -1 : items.ToList().FindIndex(i => i.Id == selectedId);
        if (idx >= 0) List.SelectedIndex = idx;
        List.EndUpdate();
    }

    public void ApplyTheme()
    {
        var p = Theme.Current;
        List.BackColor = p.Control;
        List.ForeColor = p.Text;
        List.Font = Theme.Body;
        int itemHeight = (int)(TextRenderer.MeasureText("Ag", Theme.Body).Height + 16 * Ui.S(this));
        if (List.ItemHeight != itemHeight)
        {
            List.ItemHeight = itemHeight;
            Reinsert();
        }
        _empty.BackColor = p.Control;
        _empty.ForeColor = p.TextSecondary;
        _empty.Font = Theme.Body;
        if (List.IsHandleCreated) Native.SetWindowTheme(List.Handle, p.Dark ? "DarkMode_Explorer" : "Explorer", null);
        Invalidate(true);
    }

    void DrawPeer(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= List.Items.Count) return;
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this);
        var item = (SettingsForm.PeerItem)List.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(p.Control)) g.FillRectangle(bg, e.Bounds);
        var r = RectangleF.Inflate(e.Bounds, -2 * s, -2 * s);
        if (selected)
        {
            Ui.FillRounded(g, p.Selection, null, r, 4 * s);
            float bh = Math.Min(16 * s, r.Height - 8 * s);
            Ui.FillRounded(g, p.Accent, null, new RectangleF(r.X, r.Y + (r.Height - bh) / 2, 3 * s, bh), 1.5f * s);
        }
        var back = selected ? p.Selection : p.Control;
        int x = (int)(r.X + 12 * s);
        var name = new Rectangle(x, e.Bounds.Y, e.Bounds.Width - x, e.Bounds.Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, item.Name, Theme.Body, name, p.Text, back, flags);
        int nameW = TextRenderer.MeasureText(g, item.Name, Theme.Body, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        var ip = new Rectangle(x + nameW + (int)(8 * s), e.Bounds.Y, e.Bounds.Width, e.Bounds.Height);
        TextRenderer.DrawText(g, item.Ip, Theme.Caption, ip, p.TextSecondary, back, flags);
        if (item.Connected)
        {
            var tag = "Connected";
            int w = TextRenderer.MeasureText(tag, Theme.Caption).Width;
            var tr = new Rectangle((int)(r.Right - w - 12 * s), e.Bounds.Y, w + 4, e.Bounds.Height);
            TextRenderer.DrawText(g, tag, Theme.Caption, tr, p.Accent, back, flags);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        float s = Ui.S(this);
        e.Graphics.Clear(Ui.BackOf(this));
        bool focus = List.Focused;
        Ui.FillRounded(e.Graphics, p.Control, focus ? p.Accent : p.ControlBorder, new RectangleF(0, 0, Width, Height), 4 * s, focus ? 2 * s : 1);
    }
}

// "Made with ♥ by Coldsnap": a small clickable card that opens a web page. The heart is drawn in red with
// Segoe UI Symbol, since GDI text cannot render colour emoji.
sealed class CreditBadge : Control
{
    const string Before = "Made with ", Heart = "♥", After = " by Coldsnap";
    static readonly Color Red = Color.FromArgb(0xE8, 0x3A, 0x4C);
    bool _hover, _press;
    Font? _heartFont;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Url { get; set; } = "";

    public CreditBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Link;
        AccessibleName = "Made with love by Coldsnap (opens coldsnap.fr)";
        Padding = new Padding(12, 0, 12, 0);
        MinimumSize = new Size(0, 32);
        AutoSize = true;
    }

    Font HeartFont => _heartFont ??= new Font("Segoe UI Symbol", Font.Size, FontStyle.Regular, Font.Unit);

    protected override void OnFontChanged(EventArgs e)
    {
        _heartFont?.Dispose();
        _heartFont = null;
        base.OnFontChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
    }

    const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

    (int before, int heart, int after) Widths() => (
        TextRenderer.MeasureText(Before, Font, Size.Empty, Flags).Width,
        TextRenderer.MeasureText(Heart, HeartFont, Size.Empty, Flags).Width,
        TextRenderer.MeasureText(After, Font, Size.Empty, Flags).Width);

    public override Size GetPreferredSize(Size proposed)
    {
        var (a, b, c) = Widths();
        int h = TextRenderer.MeasureText(Before, Font, Size.Empty, Flags).Height;
        return new Size(a + b + c + Padding.Horizontal, Math.Max(h + 10, MinimumSize.Height));
    }

    void Open() { if (Url.Length > 0) Updater.OpenUrl(Url); }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Open(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { Open(); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _press = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _press = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        float s = Ui.S(this);
        var back = Ui.BackOf(this);
        g.Clear(back);
        var fill = _press ? p.ControlPressed : _hover ? p.ControlHover : p.Card;
        Ui.FillRounded(g, fill, p.CardBorder, new RectangleF(0, 0, Width, Height), Height / 2f);
        if (Focused && ShowFocusCues) Ui.FillRounded(g, Color.Transparent, p.Text, new RectangleF(0, 0, Width, Height), Height / 2f, 2 * s);
        var (a, b, _) = Widths();
        int x = Padding.Left;
        var text = _hover ? p.Text : p.TextSecondary;
        TextRenderer.DrawText(g, Before, Font, new Rectangle(x, 0, a, Height), text, fill, Flags);
        TextRenderer.DrawText(g, Heart, HeartFont, new Rectangle(x + a, 0, b, Height), Red, fill, Flags);
        TextRenderer.DrawText(g, After, Font, new Rectangle(x + a + b, 0, Width - x - a - b, Height), text, fill, Flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _heartFont?.Dispose();
        base.Dispose(disposing);
    }
}
