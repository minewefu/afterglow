using System.Collections.Concurrent;
using System.Globalization;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// Always-on telemetry black box: appends one compact line per snapshot to a
/// session file, plus event markers (applied tuning — see <see cref="FlightTuning"/> —
/// stress runs, clean shutdown). After a hard crash the file ends mid-flight — the missing
/// clean-shutdown marker plus the last recorded seconds are what
/// <see cref="CrashForensics"/> reconstructs the death from.
/// Costs ~50 bytes/second. The file is capped at <see cref="SegmentBytes"/>:
/// a long session rolls into a second segment, so disk use stays bounded
/// (four files at most: this session's two segments and the previous
/// session's) instead of growing ~4 MB a day for as long as the PC stays up.
/// Lines are written by a background thread, so a stalled disk can never
/// hold up the telemetry tick that drives the fan curve.
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    /// <summary>Roll the session file over at this size (~10 hours at 1 s polling).</summary>
    public const long SegmentBytes = 4 * 1024 * 1024;

    /// <summary>Lines buffered while the disk is stalled; beyond this the newest are dropped.</summary>
    private const int QueueCapacity = 4096;

    private readonly BlockingCollection<string> _queue = new(QueueCapacity);
    private readonly Thread _writerThread;
    private readonly string _olderPath;
    private readonly long _segmentBytes;
    private StreamWriter _writer;
    private long _bytes;

    // Last offsets marker text (writer thread only): re-emitted at the top of a
    // new segment, because the analyzer reads the final segment alone and the
    // offsets marker is deduplicated — without the carry-over a crash ten
    // hours into a session would be analyzed as "at stock".
    private string? _offsetsMarker;

    // Tuning is recorded from every thread that writes the GPU (R-43), not only
    // from the telemetry tick, so the dedup and the enqueue are one step under
    // a lock: two writers must not both pass the check, nor enqueue in the
    // opposite order to the one the check saw.
    private readonly Lock _tuningLock = new();
    private FlightTuning? _lastTuning;
    private int _disposed;

    public string CurrentPath { get; }

    public FlightRecorder(string directory)
        : this(directory, SegmentBytes)
    {
    }

    /// <summary>Test seam: a smaller segment so a roll-over can be exercised quickly.</summary>
    internal FlightRecorder(string directory, long segmentBytes)
    {
        _segmentBytes = segmentBytes;
        Directory.CreateDirectory(directory);
        CurrentPath = Path.Combine(directory, "current.log");
        _olderPath = Path.Combine(directory, "current.older.log");

        // The previous session becomes previous*.log. A rename, not a copy:
        // the old copy-then-delete rewrote the whole file at every logon.
        Rotate(CurrentPath, Path.Combine(directory, "previous.log"), dropStale: false);
        Rotate(_olderPath, Path.Combine(directory, "previous.older.log"), dropStale: true);

        _writer = Open(CurrentPath);
        Enqueue($"#flight v1 started={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

        _writerThread = new Thread(WriteLoop)
        {
            Name = "Afterglow flight recorder",
            IsBackground = true,
        };
        _writerThread.Start();
    }

    public void Record(GpuSnapshot snapshot) => Enqueue(FormatLine(snapshot));

    /// <summary>Logs the currently applied offsets alone; deduplicated, so call freely.</summary>
    public void RecordOffsets(int coreOffsetMHz, int memOffsetMHz) =>
        RecordTuning(new FlightTuning { CoreOffsetMHz = coreOffsetMHz, MemOffsetMHz = memOffsetMHz });

    /// <summary>
    /// Logs the card's tuning state (offsets, clock lock, V/F-point edits, GPU
    /// identity) when it differs from the last one logged; safe from any thread.
    /// See <see cref="TuningFlightSampler"/> for who calls it and when.
    /// </summary>
    public void RecordTuning(FlightTuning tuning)
    {
        lock (_tuningLock)
        {
            if (tuning == _lastTuning)
            {
                return;
            }

            _lastTuning = tuning;
            Marker(tuning.ToMarkerText());
        }
    }

    public void Marker(string text) => Enqueue(MarkerLine(text));

    internal static string FormatLine(GpuSnapshot s) => string.Create(
        CultureInfo.InvariantCulture,
        $"{s.Timestamp.ToUnixTimeMilliseconds()}|{s.CoreClockMHz}|{s.MemClockMHz}|{s.GpuTempC}|" +
        $"{s.MemJunctionTempC:F0}|{s.PowerW:F0}|{s.GpuUtilPct}|{s.CoreVoltageMv:F0}|{s.MaxFanPercent}");

    private static string MarkerLine(string text) => string.Create(
        CultureInfo.InvariantCulture, $"#{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {text}");

    private void Enqueue(string line)
    {
        // A full queue means the disk has been stalled for over an hour; drop
        // rather than block the caller (the telemetry thread).
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _ = _queue.TryAdd(line);
            }
            catch (InvalidOperationException)
            {
                // Completed by a concurrent Dispose.
            }
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                if (!_failed)
                {
                    Write(line);
                }
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a background thread ends the whole process —
            // before OnExit, so before the fans are handed back. Diagnostics must
            // never be the reason for that.
            Log.Warn($"Flight recorder stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Writer thread only: set when the session file can no longer be written.
    private bool _failed;

    private void Write(string line)
    {
        try
        {
            if (_bytes > _segmentBytes)
            {
                RollSegment();
            }

            _writer.WriteLine(line);
            _bytes += line.Length + Environment.NewLine.Length;
            if (line.Length > 1 && line[0] == '#' &&
                line.IndexOf(' ', StringComparison.Ordinal) is int space and > 0 &&
                line.AsSpan(space + 1).StartsWith("offsets ", StringComparison.Ordinal))
            {
                _offsetsMarker = line[(space + 1)..];

                // The tuning line is the one a crash report most needs, and
                // AutoFlush only hands it to the Windows file cache, which a
                // hard reset or bluescreen discards. Tuning changes are rare,
                // so they alone are pushed through to the disk — here, on the
                // writer thread, never on the thread that wrote the GPU (R-43).
                if (_writer.BaseStream is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
            }
        }
        catch (IOException)
        {
            // A full or vanished disk must not take the recorder thread down.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // e.g. a segment roll re-creating current.log after the data root was
            // locked down by an elevated instance: stop recording, keep running.
            _failed = true;
            Log.Warn($"Flight recorder stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RollSegment()
    {
        _writer.Dispose();
        Rotate(CurrentPath, _olderPath, dropStale: false);
        _writer = Open(CurrentPath);
        _bytes = 0;
        Write($"#flight v1 continued={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        if (_offsetsMarker is not null)
        {
            Write(MarkerLine(_offsetsMarker));
        }
    }

    private static StreamWriter Open(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    private static void Rotate(string from, string to, bool dropStale)
    {
        try
        {
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
            else if (dropStale && File.Exists(to))
            {
                // The last session never rolled over, so an older segment left
                // from an even earlier one would sit beside a previous.log it
                // does not belong to.
                File.Delete(to);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Drain, then write the clean-shutdown marker directly: written after
        // the writer thread is gone it is guaranteed last, and it can't be
        // dropped by a full queue (which would fake a crash next launch).
        _queue.CompleteAdding();
        if (!_writerThread.Join(TimeSpan.FromSeconds(3)))
        {
            // The disk is hung; the writer still owns the stream. Leave it —
            // the process is exiting and a background thread dies with it.
            return;
        }

        if (!_failed)
        {
            Write(MarkerLine("clean-shutdown"));
        }

        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
        }

        _queue.Dispose();
    }
}
