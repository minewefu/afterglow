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

    /// <summary>
    /// One queued line. <paramref name="Flush"/> pushes it through to the disk
    /// once written; <paramref name="Written"/>, when set, is completed after
    /// that — true when the line reached the disk — for a caller waiting on it
    /// (<see cref="MarkerDurable"/>).
    /// </summary>
    private readonly record struct Entry(string Line, bool Flush = false, TaskCompletionSource<bool>? Written = null);

    private readonly BlockingCollection<Entry> _queue = new(QueueCapacity);
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

    // Write-start lines whose write-end has not been written (writer thread
    // only), carried into a new segment for the same reason: a crash inside a
    // write that straddled a roll must still read as inside it.
    private readonly Dictionary<long, string> _openWrites = [];

    // A durable marker that timed out and is still queued: the disk is
    // behind, and waiting again would only stall every later write as well.
    private Task? _stalledDurable;

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
        _ = TryEnqueue(new Entry($"#flight v1 started={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));

        _writerThread = new Thread(WriteLoop)
        {
            Name = "Afterglow flight recorder",
            IsBackground = true,
        };
        _writerThread.Start();
    }

    public void Record(GpuSnapshot snapshot) => TryEnqueue(new Entry(FormatLine(snapshot)));

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

    public void Marker(string text) => TryEnqueue(new Entry(MarkerLine(text)));

    /// <summary>
    /// A marker pushed through to the disk by the writer thread once written,
    /// without the caller waiting for it: survives a hard reset a moment later.
    /// </summary>
    public void MarkerFlushed(string text) => TryEnqueue(new Entry(MarkerLine(text), Flush: true));

    /// <summary>
    /// A marker that must be on the disk before the caller goes on: a tuning
    /// write announces itself this way, so a crash the write itself causes
    /// finds it there (see <see cref="Tuning.IGpuTuner.WriteStarting"/>). The line is
    /// written in order, after everything queued before it, and the caller
    /// waits until the writer thread has flushed it — at most
    /// <paramref name="timeout"/>. True when it is on disk.
    /// <para>
    /// The cost is one FlushFileBuffers of this file per tuning write, a
    /// millisecond or so on an SSD. A disk that does not answer in time costs
    /// one timeout: until that marker is written, later ones are queued
    /// (still flushed when they are reached) and not waited for.
    /// </para>
    /// </summary>
    public bool MarkerDurable(string text, TimeSpan timeout)
    {
        string line = MarkerLine(text);
        if (Volatile.Read(ref _stalledDurable) is { IsCompleted: false })
        {
            _ = TryEnqueue(new Entry(line, Flush: true));
            return false;
        }

        var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(new Entry(line, Flush: true, Written: written)))
        {
            return false;
        }

        if (written.Task.Wait(timeout))
        {
            return written.Task.Result;
        }

        Volatile.Write(ref _stalledDurable, written.Task);
        Log.Warn($"Flight recorder: a tuning-write marker was not on disk within {timeout.TotalMilliseconds:F0} ms; " +
                 "the write went ahead, and later ones won't wait until the disk catches up.");
        return false;
    }

    /// <summary>Test seam: runs on the writer thread before each line, e.g. to stall it like a hung disk.</summary>
    internal Action<string>? BeforeWrite { get; set; }

    internal static string FormatLine(GpuSnapshot s) => string.Create(
        CultureInfo.InvariantCulture,
        $"{s.Timestamp.ToUnixTimeMilliseconds()}|{s.CoreClockMHz}|{s.MemClockMHz}|{s.GpuTempC}|" +
        $"{s.MemJunctionTempC:F0}|{s.PowerW:F0}|{s.GpuUtilPct}|{s.CoreVoltageMv:F0}|{s.MaxFanPercent}");

    private static string MarkerLine(string text) => string.Create(
        CultureInfo.InvariantCulture, $"#{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {text}");

    private bool TryEnqueue(Entry entry)
    {
        // A full queue means the disk has been stalled for over an hour; drop
        // rather than block the caller (the telemetry thread).
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                return _queue.TryAdd(entry);
            }
            catch (InvalidOperationException)
            {
                // Completed by a concurrent Dispose.
            }
        }

        return false;
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var entry in _queue.GetConsumingEnumerable())
            {
                bool landed = false;
                if (!_failed)
                {
                    BeforeWrite?.Invoke(entry.Line);
                    landed = Write(entry.Line, entry.Flush);
                }

                // Released whether or not the disk took it: a waiter must never
                // outlive the line it waits for.
                entry.Written?.TrySetResult(landed);
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

    /// <summary>Writes one line (flushed to disk when asked, or when it is a tuning line); false when the disk refused it.</summary>
    private bool Write(string line, bool flush = false)
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
                line.IndexOf(' ', StringComparison.Ordinal) is int space and > 0)
            {
                string text = line[(space + 1)..];
                if (text.StartsWith("offsets ", StringComparison.Ordinal))
                {
                    _offsetsMarker = text;

                    // The tuning line is the one a crash report most needs, and
                    // AutoFlush only hands it to the Windows file cache, which a
                    // hard reset or bluescreen discards. Tuning changes are rare,
                    // so they alone are pushed through to the disk — here, on the
                    // writer thread, never on the thread that wrote the GPU (R-43).
                    flush = true;
                }
                else if (FlightWrite.TryParseStart(text, out var started))
                {
                    _openWrites[started.Id] = line;
                }
                else if (FlightWrite.TryParseEnd(text, out long ended))
                {
                    _ = _openWrites.Remove(ended);
                }
            }

            if (flush && _writer.BaseStream is FileStream file)
            {
                file.Flush(flushToDisk: true);
            }

            return true;
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

        return false;
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

        // Verbatim, with the time the write started: it is still in flight.
        foreach (string open in _openWrites.Values.ToList())
        {
            Write(open, flush: true);
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
