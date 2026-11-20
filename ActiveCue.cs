using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Media;
using System.Runtime.InteropServices;

namespace ShareKM;

// Shows which PC the keyboard and mouse just landed on: a soft glow that pulses twice around every screen of this PC,
// and/or three tiny low notes. Both are click-through and never take focus, so typing carries on undisturbed.
sealed class ActiveCue : IDisposable
{
    const int PulseMs = 1100, TickMs = 15;

    readonly SoundPlayer _chime = new(new MemoryStream(ChimeWav()));
    readonly List<Glow> _glows = [];
    readonly System.Windows.Forms.Timer _timer = new() { Interval = TickMs };
    long _started, _last;

    public ActiveCue()
    {
        try { _chime.Load(); } catch { }
        _timer.Tick += (_, _) => Tick();
    }

    public void Show(Color color, bool flash, bool chime)
    {
        long now = Environment.TickCount64;
        if (now - _last < 400) return; // one switch can report itself twice (stop forwarding, then remote control)
        _last = now;
        if (chime) try { _chime.Play(); } catch { }
        if (!flash) return;
        Clear();
        foreach (var s in Screen.AllScreens)
            try { _glows.Add(new Glow(s.Bounds, color)); } catch { }
        _started = now;
        Tick();
        _timer.Start();
    }

    void Tick()
    {
        double t = (Environment.TickCount64 - _started) / (double)PulseMs;
        if (t >= 1) { Clear(); return; }
        double s = Math.Sin(2 * Math.PI * t); // two soft breaths, starting and ending at zero
        byte alpha = (byte)Math.Round(255 * s * s);
        foreach (var g in _glows) g.SetAlpha(alpha);
    }

    void Clear()
    {
        _timer.Stop();
        foreach (var g in _glows) g.Dispose();
        _glows.Clear();
    }

    public void Dispose()
    {
        Clear();
        _timer.Dispose();
        _chime.Dispose();
    }

    // A full-screen, per-pixel-alpha layered window: transparent except a gradient a few pixels deep along the edges.
    sealed class Glow : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        readonly IntPtr _screenDc, _memDc, _bitmap, _old;
        readonly Rectangle _bounds;

        public Glow(Rectangle bounds, Color color)
        {
            _bounds = bounds;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;

            int depth = Math.Max(8, bounds.Height / 80); // ~13 px on 1080p, 18 px on 1440p
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                for (int i = 0; i < depth; i++)
                {
                    double f = 1 - i / (double)depth;
                    using var pen = new Pen(Color.FromArgb((int)(170 * f * f), color));
                    g.DrawRectangle(pen, i, i, bounds.Width - 1 - 2 * i, bounds.Height - 1 - 2 * i);
                }
            }
            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);
            _bitmap = bmp.GetHbitmap(Color.FromArgb(0)); // premultiplied, as UpdateLayeredWindow wants
            _old = SelectObject(_memDc, _bitmap);
            SetAlpha(0);
            Show();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void SetAlpha(byte alpha)
        {
            var dst = new Native.POINT { x = _bounds.X, y = _bounds.Y };
            var src = new Native.POINT();
            var size = new SIZE { cx = _bounds.Width, cy = _bounds.Height };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
        }

        bool _freed;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_freed) return;
            _freed = true;
            SelectObject(_memDc, _old);
            DeleteObject(_bitmap);
            DeleteDC(_memDc);
            ReleaseDC(IntPtr.Zero, _screenDc);
        }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Native.POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref Native.POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    }

    // Three tiny soft notes stepping down (D4, B3, G3), about a quarter of a second, quiet. Built in memory: no file to ship.
    // No sub-bass (small speakers distort on it), and every note fades in and out smoothly so nothing clicks.
    // It starts with 80 ms of silence: speakers and USB audio that went to sleep wake up on that instead of
    // chopping or popping the first note.
    static byte[] ChimeWav()
    {
        const int rate = 44100;
        const double lead = 0.08;
        (double freq, double start, double dur)[] notes = [(293.66, 0, 0.06), (246.94, 0.055, 0.06), (196.00, 0.11, 0.14)];
        int n = (int)(rate * (lead + 0.11 + 0.14 + 0.01));
        var samples = new short[n];
        foreach (var (freq, start, dur) in notes)
        {
            int from = (int)(rate * (lead + start)), len = (int)(rate * dur);
            for (int i = 0; i < len && from + i < n; i++)
            {
                double t = i / (double)rate;
                double attack = t < 0.008 ? 0.5 - 0.5 * Math.Cos(Math.PI * t / 0.008) : 1;
                double release = 0.5 + 0.5 * Math.Cos(Math.PI * i / len); // smooth all the way to zero at the note's end
                double env = attack * release * Math.Exp(-t * 12);
                double ph = 2 * Math.PI * freq * t;
                double v = env * (Math.Sin(ph) + 0.15 * Math.Sin(2 * ph));
                samples[from + i] = (short)Math.Clamp(samples[from + i] + v * 0.22 * short.MaxValue, short.MinValue, short.MaxValue);
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
