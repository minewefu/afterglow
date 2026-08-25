using System.Diagnostics;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// Fixed-capacity ring of snapshots for one GPU. Writes come from the polling
/// thread; reads may come from any thread.
/// </summary>
public sealed class SnapshotHistory
{
    private readonly GpuSnapshot?[] _buffer;
    private readonly object _lock = new();
    private int _next;
    private int _count;
    private GpuSnapshot? _latest;

    public SnapshotHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new GpuSnapshot?[capacity];
    }

    public GpuSnapshot? Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    public void Add(GpuSnapshot snapshot)
    {
        lock (_lock)
        {
            _buffer[_next] = snapshot;
            _next = (_next + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
            _latest = snapshot;
        }
    }

    /// <summary>Snapshots in chronological order (oldest first).</summary>
    public GpuSnapshot[] GetAll()
    {
        lock (_lock)
        {
            var result = new GpuSnapshot[_count];
            int start = (_next - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length]!;
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
            _latest = null;
        }
    }
}

/// <summary>
/// Owns the background polling loop: reads all GPUs at a configurable interval,
/// stores history, and raises <see cref="SnapshotTaken"/> (on the polling thread —
/// subscribers must marshal to their own context).
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private readonly IReadOnlyList<ISensorSource> _pollers;
    private readonly Dictionary<uint, SnapshotHistory> _history = [];
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private long _intervalMs;

    public TelemetryService(IReadOnlyList<ISensorSource> pollers, TimeSpan? interval = null, int historyCapacity = 3600)
    {
        _pollers = pollers;
        _intervalMs = (long)(interval ?? TimeSpan.FromSeconds(1)).TotalMilliseconds;
        foreach (var poller in pollers)
        {
            _history[poller.DeviceIndex] = new SnapshotHistory(historyCapacity);
        }
    }

    /// <summary>Polling interval; takes effect on the next tick. Clamped to 50 ms–10 s.</summary>
    public TimeSpan Interval
    {
        get => TimeSpan.FromMilliseconds(Interlocked.Read(ref _intervalMs));
        set => Interlocked.Exchange(ref _intervalMs, Math.Clamp((long)value.TotalMilliseconds, 50, 10_000));
    }

    /// <summary>Raised after each snapshot, on the polling thread.</summary>
    public event Action<GpuSnapshot>? SnapshotTaken;

    /// <summary>Raised when a poll cycle throws unexpectedly; polling continues.</summary>
    public event Action<Exception>? PollError;

    public SnapshotHistory HistoryFor(uint deviceIndex) => _history[deviceIndex];

    public IReadOnlyCollection<uint> DeviceIndices => _history.Keys;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(PollLoop)
        {
            Name = "Afterglow telemetry",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void PollLoop()
    {
        var stopwatch = new Stopwatch();
        while (!_stop.IsSet)
        {
            stopwatch.Restart();
            foreach (var poller in _pollers)
            {
                try
                {
                    var snapshot = poller.Poll();
                    _history[poller.DeviceIndex].Add(snapshot);
                    SnapshotTaken?.Invoke(snapshot);
                }
                catch (Exception ex)
                {
                    PollError?.Invoke(ex);
                }
            }

            long remaining = Interlocked.Read(ref _intervalMs) - stopwatch.ElapsedMilliseconds;
            if (remaining > 0)
            {
                _stop.Wait((int)remaining);
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _stop.Dispose();
    }
}
