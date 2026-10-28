namespace ShareKM;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // A copy started by "Run as administrator" (or by turning it off again) waits for the old one to exit.
        bool relaunch = args.Contains(Elevation.RelaunchFlag, StringComparer.OrdinalIgnoreCase) | Elevation.TakeMarker();
        var mutex = SingleInstance.Acquire(relaunch ? 6000 : 0);
        if (mutex == null) return;
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApp(relaunch || args.Contains("--settings", StringComparer.OrdinalIgnoreCase), relaunch));
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }
}
