using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// Synthetic GPU that behaves like a plausible RTX card under a fluctuating
/// gaming load. Used by `--demo` mode so the UI, CI, screenshots, and
/// contributors without NVIDIA hardware all have live-looking data.
/// </summary>
public sealed class DemoSensorSource : ISensorSource
{
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private double _temp = 42;
    private double _hotSpot = 52;
    private double _memJunction = 48;
    private double _fanDuty;
    private double _energyWh;
    private DateTimeOffset _lastPoll;

    public uint DeviceIndex { get; }

    public string Name { get; } = "Afterglow Demo GPU (synthetic)";

    public DemoSensorSource(uint deviceIndex = 0)
    {
        DeviceIndex = deviceIndex;
        _lastPoll = _start;
    }

    public GpuSnapshot Poll()
    {
        var now = DateTimeOffset.Now;
        double t = (now - _start).TotalSeconds;
        double dt = Math.Clamp((now - _lastPoll).TotalSeconds, 0.05, 5);
        _lastPoll = now;
        return Compute(now, t, dt);
    }

    /// <summary>
    /// Seeds a history ring with the past <paramref name="seconds"/> of synthetic
    /// data so demo-mode graphs are alive from the first frame.
    /// </summary>
    public void Backfill(SnapshotHistory history, int seconds)
    {
        var now = DateTimeOffset.Now;
        for (int i = seconds; i > 0; i--)
        {
            history.Add(Compute(now.AddSeconds(-i), seconds - i, 1));
        }

        _lastPoll = now;
    }

    private GpuSnapshot Compute(DateTimeOffset now, double t, double dt)
    {

        // Load: slow wave + medium wave + jitter, clamped to [3, 100].
        double load = 55
            + (35 * Math.Sin(t / 19.0))
            + (12 * Math.Sin(t / 3.7))
            + (6 * Math.Sin(t * 1.9));
        load = Math.Clamp(load, 3, 100);

        // Clocks follow load; memory clock steps between idle/perf states.
        double coreClock = 1200 + (load / 100.0 * 1650) + (25 * Math.Sin(t * 2.3));
        double memClock = load > 20 ? 14001 : 7001;

        // Power follows load with some curvature.
        double power = 45 + (Math.Pow(load / 100.0, 1.35) * 500) + (8 * Math.Sin(t * 1.1));

        // Temperatures approach a load-dependent target with first-order lag.
        double tempTarget = 38 + (load / 100.0 * 34);
        _temp += (tempTarget - _temp) * (1 - Math.Exp(-dt / 12.0));
        _hotSpot += ((tempTarget + 12) - _hotSpot) * (1 - Math.Exp(-dt / 10.0));
        _memJunction += ((tempTarget + 8) - _memJunction) * (1 - Math.Exp(-dt / 16.0));

        // Fans: zero-RPM below 45 °C, then a simple curve.
        double fanTarget = _temp <= 45 ? 0 : Math.Clamp((_temp - 40) * 2.2, 30, 100);
        _fanDuty += (fanTarget - _fanDuty) * (1 - Math.Exp(-dt / 3.0));
        uint fanPct = (uint)Math.Round(_fanDuty < 5 ? 0 : _fanDuty);
        uint fanRpm = fanPct == 0 ? 0 : (uint)(600 + (fanPct * 22));

        _energyWh += power * dt / 3600.0;

        var throttle = NvmlClocksEventReasons.None;
        if (power > 545)
        {
            throttle |= NvmlClocksEventReasons.SwPowerCap;
        }

        if (_temp > 68)
        {
            throttle |= NvmlClocksEventReasons.SwThermalSlowdown;
        }

        if (load < 8)
        {
            throttle |= NvmlClocksEventReasons.GpuIdle;
        }

        return new GpuSnapshot
        {
            Timestamp = now,
            DeviceIndex = DeviceIndex,
            CoreClockMHz = (uint)coreClock,
            MemClockMHz = (uint)memClock,
            VideoClockMHz = (uint)(coreClock * 0.75),
            GpuTempC = (uint)Math.Round(_temp),
            HotSpotTempC = Math.Round(_hotSpot, 1),
            MemJunctionTempC = Math.Round(_memJunction, 1),
            GpuUtilPct = (uint)load,
            MemCtrlUtilPct = (uint)(load * 0.62),
            EncoderUtilPct = 0,
            DecoderUtilPct = 0,
            VramUsedBytes = (ulong)((6.5 + (load / 100.0 * 9.5)) * 1024 * 1024 * 1024),
            VramTotalBytes = 32UL * 1024 * 1024 * 1024,
            PowerW = Math.Round(power, 1),
            PowerAvgW = Math.Round(power, 1),
            PowerLimitW = 575,
            EnergyWh = Math.Round(_energyWh, 3),
            CoreVoltageMv = Math.Round(700 + (load / 100.0 * 350), 0),
            PerfState = load > 15 ? 0u : 8u,
            ThrottleReasons = throttle,
            ThrottleMarginC = (int)Math.Max(0, 90 - _temp - 12),
            FanPercents = [fanPct, fanPct, (uint)Math.Max(0, (int)fanPct - 2)],
            FanRpms = [fanRpm, fanRpm, fanRpm == 0 ? 0 : fanRpm - 40],
            PcieTxKBps = (uint)(load * 2500),
            PcieRxKBps = (uint)(load * 11000),
        };
    }
}
