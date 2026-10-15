namespace Clicker.Core;

/// <summary>
/// One running instance per block. Handles Hold / Toggle / Combo modes,
/// CPS timing with random jitter, duty cycle hold time and click limit.
///
/// Edges come from GlobalHook (which filters our own synthetic input via the
/// INJECTED flag — GetAsyncKeyState polling cannot do that, our own clicks
/// would look like physical releases and kill Combo after one click).
/// Clicks run on a dedicated Highest-priority thread with Stopwatch-precise
/// waits; mouse buttons go through mouse_event (like the original
/// SpeedAutoClicker), keyboard through SendInput.
/// </summary>
public sealed class BlockRunner : IDisposable
{
    private readonly ClickBlock _block;
    private readonly GlobalHook _hook;
    private readonly Random _rnd = new();
    private readonly object _lock = new();
    private Thread? _thread;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _delayThread;
    private readonly ManualResetEvent _delayStop = new(false);
    private int _delayGen;
    private volatile bool _runningFlag;
    private bool _toggleOn;
    private bool _armed;          // combo mode
    private bool _disposed;
    private long _clicksDone;

    public event Action? StateChanged;

    /// <summary>Fired when the block is switched on/off by its activation key.</summary>
    public event Action<bool>? Toggled;

    public ClickBlock Block => _block;

    public BlockRunner(ClickBlock block, GlobalHook hook)
    {
        _block = block;
        _hook = hook;
        _hook.Pressed += OnPressed;
        _hook.Released += OnReleased;
    }

    public bool IsRunning => _runningFlag;

    public bool Armed
    {
        get { lock (_lock) return _armed; }
    }

    public bool ToggleOn
    {
        get { lock (_lock) return _toggleOn; }
    }

    public long ClicksDone => Interlocked.Read(ref _clicksDone);

    private void OnPressed(string id)
    {
        if (_disposed || !_block.Enabled) return;
        if (_block.Mode == ClickMode.Hold)
        {
            if (id == _block.ActivationKey) { StartLoopDelayed(); FireToggled(true); }
        }
        else if (_block.Mode == ClickMode.Toggle)
        {
            if (id == _block.ActivationKey)
            {
                bool on;
                lock (_lock) { _toggleOn = !_toggleOn; on = _toggleOn; }
                if (on) StartLoopDelayed(); else { CancelPending(); StopLoop(); }
                FireState();
                FireToggled(on);
            }
        }
        else // Combined
        {
            if (id == _block.ActivationKey)
            {
                bool nowArmed;
                lock (_lock) { _armed = !_armed; nowArmed = _armed; }
                if (!nowArmed) { CancelPending(); StopLoop(); }
                else if (KeyState.IsDown(_block.ClickKey)) StartLoopDelayed(); // already held
                FireState();
                FireToggled(nowArmed);
            }
            else if (id == _block.ClickKey)
            {
                bool armed;
                lock (_lock) armed = _armed;
                if (armed) StartLoopDelayed();
            }
        }
    }

    private void OnReleased(string id)
    {
        if (_disposed) return;
        if (_block.Mode == ClickMode.Hold)
        {
            if (id == _block.ActivationKey) { CancelPending(); StopLoop(); FireToggled(false); }
        }
        else if (_block.Mode == ClickMode.Combined)
        {
            if (id == _block.ClickKey) { CancelPending(); StopLoop(); }
        }
    }

    /// <summary>
    /// Start honoring DelayMs: 0 = instant (exactly the old path),
    /// n = wait n ms while the key is still held, then start.
    /// The steady-state loop is untouched, so CPS is unaffected.
    /// </summary>
    private void StartLoopDelayed()
    {
        int ms = Num.Clamp(_block.DelayMs, 0, 1000);
        if (ms <= 0) { StartLoop(); return; }
        int gen;
        lock (_lock)
        {
            try { _delayStop.Set(); } catch { }
            try { _delayStop.Reset(); } catch { }
            gen = ++_delayGen;
            _delayThread = new Thread(() => DelayThenStart(gen))
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = "DamnClick start delay",
            };
            _delayThread.Start();
        }
    }

    private void DelayThenStart(int gen)
    {
        try
        {
            int ms = Num.Clamp(_block.DelayMs, 0, 1000);
            if (!Win32.PreciseWait(ms, _delayStop)) return;
            lock (_lock)
            {
                // Same lock as CancelPending/StopLoop: either we start first and
                // the release stops us right after, or the release wins and we bail.
                if (gen != _delayGen) return;
                if (_delayStop.WaitOne(0)) return;
                if (_disposed || !_block.Enabled) return;
                StartLoop();
            }
        }
        catch { }
    }

    private void CancelPending()
    {
        lock (_lock)
        {
            try { _delayStop.Set(); } catch { }
            _delayGen++;
        }
    }

    private void StartLoop()
    {
        lock (_lock)
        {
            if (_runningFlag || (_thread != null && _thread.IsAlive)) return;
            _stop.Reset();
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "VimeClicker click loop",
            };
            _runningFlag = true;
            _thread.Start();
        }
        FireState();
    }

    private void StopLoop()
    {
        try { _stop.Set(); } catch { }
        FireState();
    }

    private void Loop()
    {
        Interlocked.Exchange(ref _clicksDone, 0);
        try
        {
            while (!_stop.WaitOne(0))
            {
                double cps = Num.Clamp(_block.Cps, 0.5, 1000);
                double baseIntervalMs = 1000.0 / cps;
                double jitter = 0;
                if (_block.RandomPercent > 0)
                {
                    double r = (_rnd.NextDouble() * 2.0 - 1.0) * (_block.RandomPercent / 100.0);
                    jitter = baseIntervalMs * r;
                }
                double interval = Math.Max(1.0, baseIntervalMs + jitter);
                double duty = Num.Clamp(_block.DutyCycle, 5, 95) / 100.0;
                int holdMs = Math.Max(1, (int)(interval * duty));
                int restMs = Math.Max(0, (int)(interval - holdMs));

                Win32.EmitClick(_block.ClickKey, holdMs, _stop);
                if (_stop.WaitOne(0)) break;
                long n = Interlocked.Increment(ref _clicksDone);

                if (!_block.Unlimited && _block.ClickLimit > 0 && n >= _block.ClickLimit)
                {
                    lock (_lock) { _toggleOn = false; }
                    break;
                }
                if (!Win32.PreciseWait(restMs, _stop)) break;
            }
        }
        catch { }
        finally
        {
            _runningFlag = false;
            FireState();
        }
    }

    private void FireState()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    private void FireToggled(bool on)
    {
        try { Toggled?.Invoke(on); } catch { }
    }

    public void RefreshAfterEdit()
    {
        // if disabled while running -> stop
        if (!_block.Enabled) { CancelPending(); StopLoop(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.Pressed -= OnPressed;
        _hook.Released -= OnReleased;
        CancelPending();
        try { _stop.Set(); } catch { }
        try { _thread?.Join(500); } catch { }
        try { _delayThread?.Join(300); } catch { }
        try { _stop.Dispose(); } catch { }
        try { _delayStop.Dispose(); } catch { }
    }
}
