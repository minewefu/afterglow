using System.Globalization;
using System.Text;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// Writes snapshots to a CSV file (HWiNFO-style sensor logging). One row per
/// snapshot; flushed every few rows so a crash loses at most a moment of data.
/// Files rotate by size to keep long sessions manageable.
/// </summary>
public sealed class CsvLogger : IDisposable
{
    public const long MaxFileBytes = 256 * 1024 * 1024;

    private static readonly string[] Columns =
    [
        "timestamp",
        "gpu_index",
        "core_clock_mhz",
        "mem_clock_mhz",
        "video_clock_mhz",
        "gpu_temp_c",
        "hotspot_temp_c",
        "mem_junction_temp_c",
        "gpu_util_pct",
        "mem_ctrl_util_pct",
        "encoder_util_pct",
        "decoder_util_pct",
        "vram_used_mib",
        "power_w",
        "power_avg_w",
        "power_limit_w",
        "core_voltage_mv",
        "perf_state",
        "throttle_reasons",
        "fan_pct_max",
        "fan_rpm_max",
        "pcie_tx_kbps",
        "pcie_rx_kbps",
    ];

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _basePath;
    private int _rotation;
    private int _rowsSinceFlush;

    public string? CurrentFile { get; private set; }

    public CsvLogger(string? basePath = null)
    {
        _basePath = basePath ?? Path.Combine(
            AppPaths.LogsDir,
            $"afterglow-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _writer is not null;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_writer is not null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_basePath)!);
            OpenWriter(_basePath);
        }
    }

    private void OpenWriter(string path)
    {
        _writer = new StreamWriter(path, append: false, Encoding.UTF8);
        _writer.WriteLine(string.Join(',', Columns));
        CurrentFile = path;
    }

    public void Log(GpuSnapshot s)
    {
        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            var sb = new StringBuilder(192);
            sb.Append(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            Append(sb, s.DeviceIndex);
            Append(sb, s.CoreClockMHz);
            Append(sb, s.MemClockMHz);
            Append(sb, s.VideoClockMHz);
            Append(sb, s.GpuTempC);
            Append(sb, s.HotSpotTempC);
            Append(sb, s.MemJunctionTempC);
            Append(sb, s.GpuUtilPct);
            Append(sb, s.MemCtrlUtilPct);
            Append(sb, s.EncoderUtilPct);
            Append(sb, s.DecoderUtilPct);
            Append(sb, s.VramUsedBytes is ulong vram ? vram / (1024.0 * 1024.0) : null);
            Append(sb, s.PowerW);
            Append(sb, s.PowerAvgW);
            Append(sb, s.PowerLimitW);
            Append(sb, s.CoreVoltageMv);
            Append(sb, s.PerfState);
            sb.Append(',').Append(s.ThrottleReasons is { } tr ? tr.ToString().Replace(", ", "|", StringComparison.Ordinal) : string.Empty);
            Append(sb, s.MaxFanPercent);
            Append(sb, MaxOrNull(s.FanRpms));
            Append(sb, s.PcieTxKBps);
            Append(sb, s.PcieRxKBps);
            _writer.WriteLine(sb.ToString());

            if (++_rowsSinceFlush >= 5)
            {
                _rowsSinceFlush = 0;
                _writer.Flush();
                RotateIfNeeded();
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (_writer is null || CurrentFile is null)
        {
            return;
        }

        if (_writer.BaseStream.Length < MaxFileBytes)
        {
            return;
        }

        _writer.Dispose();
        _rotation++;
        string next = Path.ChangeExtension(_basePath, null) + $".{_rotation}.csv";
        OpenWriter(next);
    }

    private static uint? MaxOrNull(IReadOnlyList<uint>? values)
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        uint max = 0;
        foreach (uint v in values)
        {
            max = Math.Max(max, v);
        }

        return max;
    }

    private static void Append(StringBuilder sb, double? value)
    {
        sb.Append(',');
        if (value is double d)
        {
            sb.Append(d.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static void Append(StringBuilder sb, uint? value)
    {
        sb.Append(',');
        if (value is uint u)
        {
            sb.Append(u.ToString(CultureInfo.InvariantCulture));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose() => Stop();
}
