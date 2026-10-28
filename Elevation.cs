using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ShareKM;

// Running ShareKM as administrator (opt-in). Windows keeps a normal process from seeing or sending input to
// elevated windows (UIPI), so ShareKM can restart itself elevated, and back.
static class Elevation
{
    public const string RelaunchFlag = "--relaunch";
    public enum StartResult { Started, Cancelled, Failed }

    public static bool IsElevated { get; } = Check();

    static bool Check()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Explorer passes no arguments to what it starts, so a restart is also announced with a short-lived file.
    static string MarkerPath => Path.Combine(Path.GetDirectoryName(Config.FilePath)!, "relaunch.flag");

    public static void SetMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.UtcNow.Ticks.ToString());
        }
        catch { }
    }

    public static void ClearMarker()
    {
        try { File.Delete(MarkerPath); } catch { }
    }

    // True if a restart was announced in the last 30 s; the marker is used up.
    public static bool TakeMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
            bool fresh = long.TryParse(File.ReadAllText(MarkerPath), out var t) && IsFresh(t, DateTime.UtcNow.Ticks);
            ClearMarker();
            return fresh;
        }
        catch { return false; }
    }

    public static bool IsFresh(long markerTicks, long nowTicks) =>
        nowTicks - markerTicks is >= 0 and < 30 * TimeSpan.TicksPerSecond;

    // Shows the UAC prompt (blocks until it is answered, so call it off the UI thread).
    public static (StartResult, string?) StartElevated()
    {
        SetMarker();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RelaunchFlag) { UseShellExecute = true, Verb = "runas" });
            return (StartResult.Started, null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { ClearMarker(); return (StartResult.Cancelled, null); } // ERROR_CANCELLED
        catch (Exception e) { ClearMarker(); return (StartResult.Failed, e.Message); }
    }

    // From an elevated process: Explorer runs without elevation, so a program it starts does too.
    public static (StartResult, string?) StartUnelevated()
    {
        SetMarker();
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { Environment.ProcessPath! }, UseShellExecute = false });
            return (StartResult.Started, null);
        }
        catch (Exception e) { ClearMarker(); return (StartResult.Failed, e.Message); }
    }

    // Waits (without blocking the UI) until another ShareKM process shows up.
    public static async Task<bool> WaitForOtherInstance(int ms)
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var others = Process.GetProcessesByName(name);
            bool found = others.Any(p => p.Id != Environment.ProcessId);
            foreach (var p in others) p.Dispose();
            if (found) return true;
            await Task.Delay(200);
        }
        return false;
    }
}

static class SingleInstance
{
    public const string Name = @"Local\ShareKM.SingleInstance";

    // Returns the owned mutex, or null if another ShareKM keeps it for longer than waitMs. A copy running at a
    // different elevation level can make the mutex inaccessible (UnauthorizedAccessException): that also means
    // "another copy is running", so keep polling until it goes away.
    public static Mutex? Acquire(int waitMs, string name = Name)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var m = new Mutex(true, name, out bool created);
                if (created) return m;
                try
                {
                    int left = (int)Math.Max(0, waitMs - sw.ElapsedMilliseconds);
                    if (m.WaitOne(left)) return m;
                }
                catch (AbandonedMutexException) { return m; } // previous owner exited without releasing: ours now
                m.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException) { }
            if (sw.ElapsedMilliseconds >= waitMs) return null;
            Thread.Sleep(200);
        }
    }
}

enum StartupMechanism { None, RunEntry, Task }

// "Start with Windows": a Run entry normally; a scheduled task in administrator mode, because a Run entry cannot
// start an elevated program without a UAC prompt at every sign-in. Creating or deleting the task needs elevation.
static class Autostart
{
    public const string TaskName = "ShareKM";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // The task is only used when ShareKM really runs as administrator; otherwise (also while administrator mode is
    // pending) the Run entry. An elevated start converts one into the other (see TrayApp).
    public static StartupMechanism Plan(bool enabled, bool runAsAdminActive) =>
        !enabled ? StartupMechanism.None : runAsAdminActive ? StartupMechanism.Task : StartupMechanism.RunEntry;

    public static bool RunEntryExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(TaskName) is string v && v.Trim('"').Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool TaskExists() => Schtasks(["/Query", "/TN", TaskName]).code == 0;

    public static bool IsEnabled() => RunEntryExists() || TaskExists();

    // Returns an error message, or null when done.
    public static string? Apply(StartupMechanism m, bool elevated)
    {
        try
        {
            if (m == StartupMechanism.Task)
            {
                if (!elevated) return "Starting as administrator with Windows needs ShareKM to run as administrator first.";
                var err = CreateTask();
                if (err != null) return err;
                SetRunEntry(false);
                return null;
            }
            SetRunEntry(m == StartupMechanism.RunEntry);
            if (TaskExists())
            {
                var (code, output) = Schtasks(["/Delete", "/TN", TaskName, "/F"]);
                if (code != 0)
                    return elevated ? $"Could not remove the startup task: {output}"
                        : "The administrator startup task can only be removed while ShareKM runs as administrator.";
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    static void SetRunEntry(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(TaskName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(TaskName, false);
    }

    static string? CreateTask()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ShareKM-task-{Guid.NewGuid():N}.xml");
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            using (var w = new StreamWriter(file, false, Encoding.Unicode)) TaskXml(Environment.ProcessPath!, sid).Save(w);
            var (code, output) = Schtasks(["/Create", "/TN", TaskName, "/XML", file, "/F"]);
            return code == 0 ? null : $"Could not create the startup task: {output}";
        }
        finally { try { if (File.Exists(file)) File.Delete(file); } catch { } }
    }

    // Logon of this user, highest run level, fine on battery, no time limit, never a second copy.
    public static XDocument TaskXml(string exe, string userSid) => new(
        new XDeclaration("1.0", "UTF-16", null),
        new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "ColdSnap"),
                new XElement(Ns + "Description", "Starts ShareKM as administrator when you sign in."),
                new XElement(Ns + "URI", @"\" + TaskName)),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "UserId", userSid))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "false"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", "4")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec", new XElement(Ns + "Command", exe)))));

    static (int code, string output) Schtasks(string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, "schtasks did not finish"); }
        return (p.ExitCode, output.Trim());
    }
}
