using System.Text.Json;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

public class AutomationRuleInputTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]    // overflows to +∞ instead of failing to parse
    [InlineData("-1e999")]
    [InlineData("")]
    [InlineData("hot")]
    [InlineData(null)]
    public void Nonfinite_or_unreadable_thresholds_are_refused(string? text)
    {
        Assert.False(AutomationRuleInput.TryParseThreshold(text, out _));
    }

    [Theory]
    [InlineData("94", 94)]
    [InlineData(" 94.5 ", 94.5)]
    [InlineData("3.5e2", 350)]
    public void Ordinary_thresholds_parse(string text, double expected)
    {
        Assert.True(AutomationRuleInput.TryParseThreshold(text, out double threshold));
        Assert.Equal(expected, threshold);
    }

    [Fact]
    public void A_nonfinite_threshold_cannot_be_saved()
    {
        // Why the box must refuse one: JSON has no NaN or infinity, so saving the
        // settings throws — and AppServices had already taken the rule into
        // memory, so every later settings save threw too (R-37).
        var settings = new AppSettings
        {
            AutomationRules = [new AutomationRule { Metric = "gpu", Threshold = double.PositiveInfinity, Action = "reset" }],
        };

        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(settings));
    }
}
