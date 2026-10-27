using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// An in-memory IGCL adapter for <see cref="IntelSensorSource"/>: a scripted
/// sequence of bulk-telemetry blocks (one per poll, the last repeating) plus
/// optional component temperature sensors. Frequency domains and memory
/// modules are absent, which the source already treats as "unavailable".
/// Read-only like the seam it implements — there is nothing here to write.
/// </summary>
internal sealed class FakeIntelTelemetryDevice : IIntelTelemetryDevice
{
    public const nint MemorySensor = 0x2000;

    private readonly CtlPowerTelemetry[] _samples;
    private int _next;

    public FakeIntelTelemetryDevice(params CtlPowerTelemetry[] samples)
    {
        _samples = samples;
    }

    /// <summary>Enumerated temperature sensors, by handle.</summary>
    public List<(nint Handle, CtlTempProperties Properties)> TemperatureSensors { get; } = [];

    /// <summary>What each sensor handle reads; a handle missing here fails its read.</summary>
    public Dictionary<nint, double> SensorReadings { get; } = [];

    public IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains() => [];

    public IReadOnlyList<(nint Handle, CtlMemProperties Properties)> GetMemoryModules() => [];

    public IReadOnlyList<(nint Handle, CtlTempProperties Properties)> GetTemperatureSensors() => TemperatureSensors;

    public CtlResult TryGetPowerTelemetry(out CtlPowerTelemetry telemetry)
    {
        telemetry = _samples[Math.Min(_next, _samples.Length - 1)];
        _next++;
        return CtlResult.Success;
    }

    public CtlResult TryGetFrequencyState(nint domain, out CtlFreqState state)
    {
        state = default;
        return CtlResult.ErrorUnsupportedFeature;
    }

    public CtlResult TryGetTemperature(nint sensor, out double temperatureC)
    {
        if (SensorReadings.TryGetValue(sensor, out temperatureC))
        {
            return CtlResult.Success;
        }

        temperatureC = 0;
        return CtlResult.ErrorUnsupportedFeature;
    }

    public CtlResult TryGetMemoryState(nint module, out CtlMemState state)
    {
        state = default;
        return CtlResult.ErrorUnsupportedFeature;
    }

    public void AddMemorySensor(double? readingC)
    {
        TemperatureSensors.Add((MemorySensor, new CtlTempProperties { Type = CtlTempSensor.Memory }));
        if (readingC is double c)
        {
            SensorReadings[MemorySensor] = c;
        }
    }

    /// <summary>A supported item; <paramref name="units"/> says how the driver labelled it.</summary>
    public static CtlTelemetryItem Item(double value, CtlUnits units)
    {
        var item = new CtlTelemetryItem { Supported = 1, Units = units, Type = CtlDataType.Double };
        item.Value.DataDouble = value;
        return item;
    }
}
