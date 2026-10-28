using System.Diagnostics;

namespace ShareKM;

// Coalesces pointer motion while forwarding. The mouse hook calls Add(); a sender thread sends the summed delta
// every 1/RateHz s when it is nonzero. Flush() is called before every button, wheel and key event and when
// forwarding stops, so those land where the pointer is. A delta is enqueued while the lock is held, so a later
// event can never overtake movement that was taken before it.
sealed class MotionPump(Action<int, int> send)
{
    public const int MinHz = 60, MaxHz = 1000, DefaultHz = 200;

    readonly object _lock = new();
    int _dx, _dy;
    volatile int _hz = DefaultHz;
    volatile object? _run;
    Thread? _thread;

    public int RateHz { get => _hz; set => _hz = Clamp(value); }

    // Invalid (0 or negative) -> default; otherwise clamped to the slider range.
    public static int Clamp(int hz) => hz <= 0 ? DefaultHz : Math.Clamp(hz, MinHz, MaxHz);

    public void Add(int dx, int dy)
    {
        lock (_lock) { _dx += dx; _dy += dy; }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_dx == 0 && _dy == 0) return;
            send(_dx, _dy);
            _dx = _dy = 0;
        }
    }

    public void Start()
    {
        if (_run != null) return;
        var run = new object();
        _run = run;
        _thread = new Thread(() => Loop(run)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ShareKM motion" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = null;
        _thread?.Join(200);
        _thread = null;
        Flush();
    }

    void Loop(object run)
    {
        // High-resolution waitable timer (Windows 10 1803+) gives sub-millisecond periods; otherwise 1 ms sleeps.
        var timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (timer == IntPtr.Zero) Native.timeBeginPeriod(1);
        try
        {
            long next = Stopwatch.GetTimestamp();
            while (ReferenceEquals(_run, run))
            {
                long period = Stopwatch.Frequency / _hz, now = Stopwatch.GetTimestamp();
                next += period;
                if (next < now) next = now; // fell behind: skip ahead rather than burst
                long wait = (next - now) * 10_000_000 / Stopwatch.Frequency; // 100 ns units
                if (wait > 0)
                {
                    if (timer != IntPtr.Zero)
                    {
                        long due = -wait;
                        Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                        Native.WaitForSingleObject(timer, Native.INFINITE);
                    }
                    else Thread.Sleep((int)Math.Max(1, wait / 10_000));
                }
                Flush();
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) Native.CloseHandle(timer);
            else Native.timeEndPeriod(1);
        }
    }
}
