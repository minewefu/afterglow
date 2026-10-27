using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Tests;

/// <summary>
/// NVIDIA fan RPM polling (the NVML part of the NVAPI enricher), without a
/// driver. A tick on which no fan answers may only stop the polling for good
/// when the driver says the read is unsupported — the rule the fan-percentage
/// path in SensorPoller already follows (R-36).
/// </summary>
public sealed class FanRpmReaderTests
{
    /// <summary>A card whose fans answer with a settable status per fan, counting native calls.</summary>
    private sealed class FakeFans(params NvmlReturn[] codes)
    {
        public NvmlReturn[] Codes { get; set; } = codes;

        public int Calls { get; private set; }

        public NvmlReturn Read(uint fan, out uint rpm)
        {
            Calls++;
            rpm = 1500 + fan;
            return Codes[fan];
        }
    }

    private static void AssertRpms(uint[]? actual, params uint[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_transient_error_does_not_stop_rpm_polling()
    {
        // R-36: one GpuIsLost tick (a TDR) used to zero the fan count, so the
        // RPM tile stayed blank for the rest of the process after the driver
        // recovered — no native read was even attempted again.
        var fans = new FakeFans(NvmlReturn.Success);
        var reader = new FanRpmReader(1, fans.Read);

        AssertRpms(reader.Read(), 1500);

        fans.Codes = [NvmlReturn.GpuIsLost];
        Assert.Null(reader.Read());

        fans.Codes = [NvmlReturn.Success];
        AssertRpms(reader.Read(), 1500);
        Assert.Equal(3, fans.Calls);
    }

    [Theory]
    [InlineData(NvmlReturn.GpuIsLost)]
    [InlineData(NvmlReturn.Unknown)]
    [InlineData(NvmlReturn.Timeout)]
    [InlineData(NvmlReturn.NoPermission)]
    public void Errors_that_do_not_mean_unsupported_are_retried_every_tick(NvmlReturn code)
    {
        var fans = new FakeFans(code, code);
        var reader = new FanRpmReader(2, fans.Read);

        Assert.Null(reader.Read());
        Assert.Null(reader.Read());

        Assert.Equal(4, fans.Calls);
    }

    [Theory]
    [InlineData(NvmlReturn.NotSupported)]
    [InlineData(NvmlReturn.FunctionNotFound)]   // a driver older than the RPM export
    public void An_unsupported_read_is_latched_and_never_retried(NvmlReturn code)
    {
        var fans = new FakeFans(code, code);
        var reader = new FanRpmReader(2, fans.Read);

        Assert.Null(reader.Read());
        int callsAfterFirstTick = fans.Calls;

        fans.Codes = [NvmlReturn.Success, NvmlReturn.Success];
        Assert.Null(reader.Read());
        Assert.Equal(callsAfterFirstTick, fans.Calls);
    }

    [Fact]
    public void Unsupported_is_latched_only_when_every_fan_says_so()
    {
        // One fan unsupported and the other lost is not "this card has no RPM
        // sensor" — the lost read says nothing yet, so the next tick asks again.
        var fans = new FakeFans(NvmlReturn.NotSupported, NvmlReturn.GpuIsLost);
        var reader = new FanRpmReader(2, fans.Read);

        Assert.Null(reader.Read());

        fans.Codes = [NvmlReturn.NotSupported, NvmlReturn.Success];
        AssertRpms(reader.Read(), 0, 1501);
    }

    [Fact]
    public void A_fan_that_fails_beside_one_that_answers_reads_zero()
    {
        var fans = new FakeFans(NvmlReturn.Success, NvmlReturn.GpuIsLost);
        var reader = new FanRpmReader(2, fans.Read);

        AssertRpms(reader.Read(), 1500, 0);
    }

    [Fact]
    public void A_card_without_fans_is_never_asked()
    {
        var fans = new FakeFans();
        var reader = new FanRpmReader(0, fans.Read);

        Assert.Null(reader.Read());
        Assert.Equal(0, fans.Calls);
    }
}
