using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// Keeps one card's flight recording told what that card is tuned to (R-43).
/// The recorder used to learn this only from a once-a-minute telemetry sample
/// (ten minutes at 10 s polling), and only the two offsets: a profile applied
/// just after a stock sample could crash the machine before the next sample,
/// and the crash report then called it a stock-clock failure; a lock-only or
/// per-point-only change never reached the recording at all.
/// <para>
/// Now every tuner write is recorded as it happens — <see cref="IGpuTuner.StateWritten"/>
/// is raised after every write, whoever made it — together with the clock lock,
/// the V/F-point edits and the card's identity. The periodic re-read stays, at a
/// fixed wall-clock period, for writes this process cannot see: the CLI, the MCP
/// server and other tools.
/// </para>
/// <para>
/// <see cref="IGpuTuner.StateWritten"/> comes only after a write returns, so a
/// crash or TDR the write itself caused left the recording on the tuning before
/// it. Each write is therefore also recorded as it starts — on
/// <see cref="IGpuTuner.WriteStarting"/>, synchronously and flushed to disk
/// before the driver call goes ahead — and as it finishes. A recording that
/// ends between the two ended inside that write, and the crash report says so
/// (see <see cref="FlightSession.WriteInProgress"/>).
/// </para>
/// </summary>
public sealed class TuningFlightSampler
{
    /// <summary>How often the state is re-read for writes made outside this process.</summary>
    public static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest a tuning write waits for its start marker to reach the
    /// disk. A flush takes about a millisecond on an SSD; a disk that has not
    /// answered in this long is not going to save the evidence in time anyway,
    /// and the write is the user's.
    /// </summary>
    public static readonly TimeSpan DurableMarkerTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IGpuTuner _tuner;
    private readonly FlightRecorder _recorder;
    private readonly string? _gpuId;

    // Captures run on whatever thread wrote the GPU and on the telemetry thread
    // at once. Each takes a ticket BEFORE it reads, and only a capture newer
    // than the last one recorded may record: a read that started before a
    // write and finished after the write's own capture would otherwise land
    // last and leave the recording stating the pre-write tuning. The lock is
    // held for the bookkeeping only, never across a driver read, so a slow
    // read on one thread never holds up a GPU write on another.
    private readonly Lock _recordLock = new();
    private long _nextTicket;
    private long _lastRecordedTicket;

    private DateTimeOffset? _lastPoll;
    private int _warned;

    public TuningFlightSampler(IGpuTuner tuner, FlightRecorder recorder, string? gpuId)
    {
        _tuner = tuner;
        _recorder = recorder;
        _gpuId = gpuId ?? tuner.GpuUuid;

        // For the app's lifetime, like the recorder it feeds. A capture raised
        // during shutdown after the recorder is disposed is dropped by the
        // recorder, and runs on the thread that just wrote the GPU — the
        // tuner is in use there anyway, so the read is never later than the
        // write it follows.
        _tuner.StateWritten += _ => Capture();

        // No driver reads here: the start runs under the tuner's lock, just
        // before the driver call, and waits only for the disk; the end is
        // flushed by the writer thread without waiting. A write's end marker
        // can lag its return by that flush, so a crash in the moment right
        // after a write may still be attributed to it — never one after a
        // later write, or after the recording moved on.
        _tuner.WriteStarting += (_, write) => RecordWriteStarting(write);
        _tuner.WriteFinished += (_, write) => _recorder.MarkerFlushed(FlightWrite.EndMarkerText(write.Id));
    }

    private void RecordWriteStarting(TuningWrite write) =>
        _ = _recorder.MarkerDurable(FlightWrite.StartMarkerText(write), DurableMarkerTimeout);

    /// <summary>
    /// Called with each telemetry snapshot's time; re-reads the state when
    /// <see cref="PollPeriod"/> has passed (or the clock went backwards).
    /// Telemetry thread only.
    /// </summary>
    public void OnSnapshot(DateTimeOffset at)
    {
        if (_lastPoll is { } last && at >= last && at - last < PollPeriod)
        {
            return;
        }

        _lastPoll = at;
        Capture();
    }

    /// <summary>Reads the card's tuning now and records it if it changed. Never throws.</summary>
    public void Capture()
    {
        long ticket = Interlocked.Increment(ref _nextTicket);
        FlightTuning state;
        try
        {
            state = Read(_tuner, _gpuId);
        }
        catch (Exception ex)
        {
            // Diagnostics must never break the write path or the telemetry
            // tick that drives the fan curve. Said once, not every period.
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Log.Warn($"Flight recorder could not read the tuning state: {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        lock (_recordLock)
        {
            if (ticket < _lastRecordedTicket)
            {
                return; // a capture that started later already recorded a fresher state
            }

            _lastRecordedTicket = ticket;
            _recorder.RecordTuning(state);
        }
    }

    /// <summary>
    /// The tuning as the diagnosis needs it. The lock is <see cref="IGpuTuner.AppliedLockMHz"/> —
    /// the lock Afterglow applied — not ReadCurrent's observation, which on Intel
    /// includes a factory ceiling and would make every stock session read as
    /// tuned. V/F points count as edited by the rule a saved profile captures
    /// them by (<see cref="VfPointPlanner.CaptureForProfile"/>): an offset that
    /// differs from the global core offset. A card without per-point control
    /// has no edits to record (0); a table that cannot be read is "not
    /// recorded" (null), never "none".
    /// </summary>
    internal static FlightTuning Read(IGpuTuner tuner, string? gpuId)
    {
        var current = tuner.ReadCurrent();
        uint? lockMHz = tuner.AppliedLockMHz;

        // A lock applied before the PC slept may or may not still be in force
        // (UnconfirmedLockMHz). Recording "lock=none" for it would let the
        // diagnosis call a crash on that card stock; the lock is "not
        // recorded" instead, so the verdict never clears Afterglow on it.
        bool lockKnown = lockMHz is not null || tuner.UnconfirmedLockMHz is null;

        int? edited = 0;
        int? highest = null;
        if (tuner.Capabilities.SupportsVfPoints)
        {
            if (tuner.TryReadVfPoints(out var points) == NvapiStatus.Ok && points.Count > 0)
            {
                edited = 0;
                foreach (var point in points)
                {
                    if (point.OffsetMHz != current.CoreOffsetMHz)
                    {
                        edited++;
                        highest = highest is int h ? Math.Max(h, point.OffsetMHz) : point.OffsetMHz;
                    }
                }
            }
            else
            {
                edited = null;
            }
        }

        return new FlightTuning
        {
            CoreOffsetMHz = current.CoreOffsetMHz,
            MemOffsetMHz = current.MemOffsetMHz,
            LockRecorded = lockKnown,
            LockedCoreClockMHz = lockMHz,
            VfPointsEdited = edited,
            VfPointMaxOffsetMHz = highest,
            GpuId = gpuId,
        };
    }
}
