using System.Globalization;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// Always-on telemetry black box: appends one compact line per snapshot to a
/// session file, plus event markers (applied offsets, stress runs, clean
/// shutdown). After a hard crash the file ends mid-flight — the missing
/// clean-shutdown marker plus the last recorded seconds are what
/// <see cref="CrashForensics"/> reconstructs the death from.
/// Costs ~100 bytes/second; the previous session is kept for analysis.
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private int _lastCoreOffset = int.MinValue;
    private int _lastMemOffset = int.MinValue;
    private bool _disposed;

    public string CurrentPath { get; }

    public FlightRecorder(string directory)
    {
        Directory.CreateDirectory(directory);
        CurrentPath = Path.Combine(directory, "current.log");
        string previous = Path.Combine(directory, "previous.log");
        try
        {
            if (File.Exists(CurrentPath))
            {
                File.Copy(CurrentPath, previous, overwrite: true);
                File.Delete(CurrentPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _writer = new StreamWriter(
            new FileStream(CurrentPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        WriteLine($"#flight v1 started={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
    }

    public void Record(GpuSnapshot snapshot)
    {
        WriteLine(FormatLine(snapshot));
    }

    /// <summary>Logs the currently applied offsets; deduplicated, so call freely.</summary>
    public void RecordOffsets(int coreOffsetMHz, int memOffsetMHz)
    {
        if (coreOffsetMHz == _lastCoreOffset && memOffsetMHz == _lastMemOffset)
        {
            return;
        }

        _lastCoreOffset = coreOffsetMHz;
        _lastMemOffset = memOffsetMHz;
        Marker($"offsets core={coreOffsetMHz} mem={memOffsetMHz}");
    }

    public void Marker(string text)
    {
        WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"#{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {text}"));
    }

    internal static string FormatLine(GpuSnapshot s) => string.Create(
        CultureInfo.InvariantCulture,
        $"{s.Timestamp.ToUnixTimeMilliseconds()}|{s.CoreClockMHz}|{s.MemClockMHz}|{s.GpuTempC}|" +
        $"{s.MemJunctionTempC:F0}|{s.PowerW:F0}|{s.GpuUtilPct}|{s.CoreVoltageMv:F0}|{s.MaxFanPercent}");

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _writer.WriteLine(line);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"#{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} clean-shutdown"));
            _writer.Dispose();
            _disposed = true;
        }
    }
}
