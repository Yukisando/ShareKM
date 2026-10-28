using Microsoft.Win32;

namespace ShareKM;

sealed record Palette(bool Dark, Color Window, Color Card, Color CardBorder, Color Text, Color TextSecondary,
    Color TextDisabled, Color Control, Color ControlHover, Color ControlPressed, Color ControlBorder, Color ControlStrong,
    Color Selection, Color Accent, Color AccentHover, Color AccentPressed, Color OnAccent);

// Windows 11 light/dark colours (approximating the WinUI tokens), the user's accent colour and Segoe UI Variable fonts.
static class Theme
{
    public static bool? ForceDark { get; set; } // previews
    public static float RenderScale { get; set; } = 1; // previews: extra scale on top of the real DPI

    public static Palette Current { get; private set; } = Load();
    public static event Action? Changed;

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
            var p = Load();
            if (p == Current) return;
            Current = p;
            Changed?.Invoke();
        };
    }

    public static void Reload()
    {
        Current = Load();
        Changed?.Invoke();
    }

    static Palette Load()
    {
        bool dark = ForceDark ?? IsSystemDark();
        var (accentLight, accentDark) = Accent();
        return dark
            ? new Palette(true, C(0x202020), C(0x2B2B2B), C(0x1D1D1D), C(0xFFFFFF), C(0xC8C8C8), C(0x787878),
                C(0x373737), C(0x3D3D3D), C(0x323232), C(0x454545), C(0x9A9A9A), C(0x3A3A3A),
                accentLight, Blend(accentLight, C(0x202020), 0.9f), Blend(accentLight, C(0x202020), 0.8f), C(0x000000))
            : new Palette(false, C(0xF3F3F3), C(0xFBFBFB), C(0xE5E5E5), C(0x1B1B1B), C(0x5F5F5F), C(0xA0A0A0),
                C(0xFEFEFE), C(0xF6F6F6), C(0xF0F0F0), C(0xD6D6D6), C(0x8A8A8A), C(0xEBEBEB),
                accentDark, Blend(accentDark, C(0xF3F3F3), 0.9f), Blend(accentDark, C(0xF3F3F3), 0.8f), C(0xFFFFFF));
    }

    static bool IsSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // Win11 uses a lighter accent shade on dark backgrounds and a darker one on light backgrounds.
    static (Color light, Color dark) Accent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] b && b.Length >= 32)
                return (Color.FromArgb(b[4], b[5], b[6]), Color.FromArgb(b[16], b[17], b[18])); // Light2, Dark1
            using var d = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (d?.GetValue("AccentColor") is int abgr)
            {
                var c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                return (Blend(c, Color.White, 0.6f), Blend(c, Color.Black, 0.85f));
            }
        }
        catch { }
        return (C(0x60CDFF), C(0x005FB8));
    }

    static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    public static Color Blend(Color a, Color b, float amountA) => Color.FromArgb(
        (int)(a.R * amountA + b.R * (1 - amountA)), (int)(a.G * amountA + b.G * (1 - amountA)), (int)(a.B * amountA + b.B * (1 - amountA)));

    // ---- fonts ----

    static readonly Dictionary<(float, bool, bool), Font> Fonts = [];

    public static Font Font(float points, bool semibold = false, bool display = false)
    {
        points *= RenderScale;
        if (Fonts.TryGetValue((points, semibold, display), out var f)) return f;
        string[] names = (display, semibold) switch
        {
            (true, true) => ["Segoe UI Variable Display Semib", "Segoe UI Semibold"],
            (true, false) => ["Segoe UI Variable Display", "Segoe UI"],
            (false, true) => ["Segoe UI Variable Text Semibold", "Segoe UI Semibold"],
            _ => ["Segoe UI Variable Text", "Segoe UI"],
        };
        foreach (var n in names)
        {
            var font = new Font(n, points);
            if (font.Name == n) return Fonts[(points, semibold, display)] = font;
            font.Dispose();
        }
        return Fonts[(points, semibold, display)] = new Font(SystemFonts.MessageBoxFont!.FontFamily, points, semibold ? FontStyle.Bold : FontStyle.Regular);
    }

    public static Font Body => Font(10.5f);          // 14 px
    public static Font BodyStrong => Font(10.5f, true);
    public static Font Caption => Font(9f);          // 12 px
    public static Font Subtitle => Font(10.5f, true);
    public static Font Title => Font(20f, true, true);

    // ---- window chrome ----

    public static void ApplyChrome(Form f)
    {
        if (!f.IsHandleCreated) return;
        int build = Environment.OSVersion.Version.Build;
        int dark = Current.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(f.Handle, build >= 19041 ? 20 : 19, ref dark, sizeof(int)); // immersive dark title bar
        if (build >= 22621)
        {
            int mica = 2; // DWMSBT_MAINWINDOW
            Native.DwmSetWindowAttribute(f.Handle, 38, ref mica, sizeof(int));
        }
    }
}
