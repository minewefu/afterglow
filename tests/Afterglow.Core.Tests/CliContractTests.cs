using System.Text.RegularExpressions;
using Afterglow.Cli;

namespace Afterglow.Core.Tests;

/// <summary>
/// The CLI's argument contract, locked down.
/// <para>
/// Two rounds of review found the same shape of bug here: an option a command
/// genuinely reads is missing from the allow-list it validates against, so a
/// documented (or hidden diagnostic) flag stops working; or an option is read
/// but never validated, so a malformed value silently becomes a default. Both
/// are invisible until someone runs the exact invocation. These tests run them.
/// </para>
/// </summary>
public class CliContractTests
{
    /// <summary>
    /// Real invocations, validated against each command's OWN declared options —
    /// not a list the test supplies. That distinction is the point: a test that
    /// passes its own allow-list proves the validator works and proves nothing
    /// about whether the command declared its flags.
    /// </summary>
    public static TheoryData<string, string[]> AcceptedInvocations() => new()
    {
        // stress: documented options plus the hidden adapter probe.
        { "stress", ["stress", "--seconds", "60"] },
        { "stress", ["stress", "--seconds", "28", "--intensity", "8192"] },
        { "stress", ["stress", "--pattern", "transitions", "--gpu", "0"] },
        { "stress", ["stress", "--probe-adapter"] },
        { "stress", ["stress"] },

        // vram / fps / certify.
        { "vram", ["vram", "--seconds", "20"] },
        { "vram", ["vram"] },
        { "fps", ["fps", "--seconds", "10"] },
        { "certify", ["certify", "--profile", "daily", "--seconds", "90", "--gpu", "1"] },

        // vfcurve: every flag combination the docs show.
        { "vfcurve", ["vfcurve", "--probe"] },
        { "vfcurve", ["vfcurve", "--load", "--seconds", "40", "--fresh", "--json"] },

        // vfpoints.
        { "vfpoints", ["vfpoints", "--gpu", "0"] },
        { "vfpoints", ["vfpoints", "--set", "120=-90"] },
        { "vfpoints", ["vfpoints", "--flatten", "875:1875"] },
        { "vfpoints", ["vfpoints", "--clear"] },

        // drs: --probe-create/list/profile are undocumented diagnostics that a
        // reader of the help text would never think to allow-list. Omitting them
        // broke all three the moment validation was added; this is that guard.
        { "drs", ["drs", "--exe", "game.exe", "--cap", "60"] },
        { "drs", ["drs", "--exe", "game.exe", "--vsync", "off", "--low-latency", "on"] },
        { "drs", ["drs", "--exe", "game.exe", "--clear"] },
        { "drs", ["drs", "--exe", "game.exe", "--probe-list"] },
        { "drs", ["drs", "--exe", "game.exe", "--probe-create"] },
        { "drs", ["drs", "--exe", "game.exe", "--probe-profile"] },
    };

    [Theory]
    [MemberData(nameof(AcceptedInvocations))]
    public void Documented_and_hidden_invocations_are_never_rejected(string command, string[] args)
    {
        Assert.Null(CliArgs.Validate(args, command));
    }

    /// <summary>
    /// Every option literal a command's source reads must be declared. This is
    /// the check that would have caught the missing `drs` probe flags without
    /// anyone thinking to write an invocation for them.
    /// </summary>
    [Theory]
    [InlineData("stress", "StressCommand")]
    [InlineData("vram", "VramCommand")]
    [InlineData("fps", "FpsCommand")]
    [InlineData("certify", "CertifyCommand")]
    [InlineData("vfcurve", "VfCurveCommand")]
    [InlineData("vfpoints", "VfPointsCommand")]
    [InlineData("drs", "DrsCommand")]
    [InlineData("monitor", "MonitorCommand")]
    [InlineData("set", "TuneCommands")]
    public void Every_option_the_command_reads_is_declared(string command, string sourceFile)
    {
        string path = Path.Combine(RepoRoot(), "src", "Afterglow.Cli", sourceFile + ".cs");
        Assert.True(File.Exists(path), $"expected {path} to exist");

        var (flags, valueOptions) = CliArgs.Options[command];
        var declared = flags.Concat(valueOptions).ToHashSet(StringComparer.Ordinal);

        var read = Regex
            .Matches(File.ReadAllText(path), "\"(--[a-z][a-z-]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // One-directional on purpose: anything the source names must be accepted,
        // and over-declaring is harmless.
        string[] undeclared = [.. read.Except(declared).Order()];
        Assert.True(
            undeclared.Length == 0,
            $"{sourceFile} names {string.Join(", ", undeclared)} but does not declare them in " +
            "CliArgs.Options, so those options are now rejected.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Afterglow.Cli")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Theory]
    [InlineData("--json")]        // stress does not emit JSON; the help text says so
    [InlineData("--secconds")]    // typo
    [InlineData("--probeadapter")]
    public void An_unknown_option_is_rejected(string option)
    {
        string? error = CliArgs.Validate(["stress", option, "5"], "stress");

        Assert.NotNull(error);
        Assert.Contains("Unknown option", error);
    }

    [Fact]
    public void A_value_option_with_no_value_is_rejected()
    {
        Assert.NotNull(CliArgs.Validate(["stress", "--seconds"], "stress"));
    }

    [Theory]
    [InlineData("6O")]     // letter O
    [InlineData("abc")]
    [InlineData("1.5")]
    public void An_unparseable_number_is_rejected_rather_than_defaulted(string value)
    {
        int seconds = 30;
        string? error = CliArgs.TryInt(["stress", "--seconds", value], "--seconds", 5, 86_400, ref seconds);

        Assert.NotNull(error);
        Assert.Equal(30, seconds);   // untouched — never half-applied
    }

    [Fact]
    public void An_out_of_range_number_is_rejected_rather_than_clamped()
    {
        int seconds = 30;
        Assert.NotNull(CliArgs.TryInt(["stress", "--seconds", "3"], "--seconds", 5, 86_400, ref seconds));
        Assert.Equal(30, seconds);
    }

    /// <summary>
    /// The GPU index decides WHICH CARD a stability verdict is about, so
    /// "absent" and "present but unusable" must never collapse into one another.
    /// They did: an unparseable index read as "no card requested", which on a
    /// multi-GPU machine burned a guessed adapter and exited 0.
    /// </summary>
    [Theory]
    [InlineData("1x")]
    [InlineData("-1")]
    [InlineData("0x1")]
    [InlineData("1.0")]
    [InlineData("GPU1")]
    public void A_malformed_gpu_index_is_an_error_not_an_absent_selection(string value)
    {
        Assert.False(CliGpu.TryParseIndex(["stress", "--gpu", value], out uint? index, out string? error));
        Assert.Null(index);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_trailing_gpu_option_is_an_error_not_an_absent_selection()
    {
        // The scan used to stop one token short of the end, so a trailing --gpu
        // was invisible and read as "no card requested".
        Assert.False(CliGpu.TryParseIndex(["set", "--core-offset", "200", "--gpu"], out uint? index, out string? error));
        Assert.Null(index);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_valid_or_absent_gpu_index_still_parses()
    {
        Assert.True(CliGpu.TryParseIndex(["stress", "--gpu", "1"], out uint? index, out _));
        Assert.Equal(1u, index);

        Assert.True(CliGpu.TryParseIndex(["stress", "--seconds", "60"], out uint? none, out _));
        Assert.Null(none);
    }

    [Fact]
    public void The_shared_validator_checks_the_gpu_index_for_every_command_at_once()
    {
        // --gpu is validated inside Validate rather than per command, so a new
        // command cannot forget it.
        Assert.NotNull(CliArgs.Validate(["vram", "--gpu", "1x"], "vram"));
    }
}
