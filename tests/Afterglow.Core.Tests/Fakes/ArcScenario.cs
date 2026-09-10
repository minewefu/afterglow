using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// The pieces every Arc clock-lock scenario starts from: one identity, one way
/// to build the real tuner on the fake device, the two profile shapes, and a
/// knob summary for assertion messages.
/// </summary>
internal static class ArcScenario
{
    public const string Uuid = "INTEL-00:02.0-E20B-0000";

    public static ArcGpuTuner Tuner(FakeArcDevice device) => new(device, Uuid);

    public static TuningProfile Lock(uint mhz) => new() { Name = "lock", LockedCoreClockMHz = mhz };

    public static TuningProfile NoLock() => new() { Name = "plain" };
}
