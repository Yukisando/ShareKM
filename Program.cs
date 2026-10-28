namespace ShareKM;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ShareKM.SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(args.Contains("--settings", StringComparer.OrdinalIgnoreCase)));
        GC.KeepAlive(mutex);
    }
}
