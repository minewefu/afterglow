using Afterglow.Cli;
using Afterglow.Core.Fans;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tests;

/// <summary>
/// `certify` runs a profile under its own fan policy (R-11). A fan curve needs
/// telemetry to move the fans, which the one-shot CLI does not run, so the CLI
/// refuses a curve profile before it opens the GPU rather than certify it under
/// some other fan configuration.
/// </summary>
public class CertifyCommandFanTests
{
    [Fact]
    public void A_curve_profile_is_sent_to_the_app()
    {
        var profile = new TuningProfile { Name = "quiet", FanMode = FanMode.Curve, FanCurve = new FanCurveConfig() };

        string? refusal = CertifyCommand.FanPolicyRefusal(profile);

        Assert.NotNull(refusal);
        Assert.Contains("Profiles page", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FanMode.Auto)]
    [InlineData(FanMode.Fixed)]
    public void Auto_and_fixed_fan_profiles_are_certified_here(FanMode mode)
    {
        Assert.Null(CertifyCommand.FanPolicyRefusal(new TuningProfile { Name = "daily", FanMode = mode }));
    }
}
