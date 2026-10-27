using Afterglow.Core.Diagnostics;
using Afterglow.Core.Metrics;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Regression tests for the "never report a number you did not measure" rule.
/// Each of these covers a place that used to print a fabricated 0 (or crash)
/// where the honest answer was "not measured".
/// </summary>
public class HonestReportingTests
{
    private static SessionReport Report(string app, double? power, double? gpuTemp, double? memJunction) => new()
    {
        Application = app,
        StartedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        DurationSeconds = 300,
        AvgFps = 120,
        Low1Fps = 96,
        P1Fps = 98,
        Frames = 36_000,
        AvgPowerW = power,
        AvgGpuTempC = gpuTemp,
        AvgMemJunctionC = memJunction,
    };

    [Fact]
    public void A_sensor_that_never_reported_is_not_exported_as_zero()
    {
        // A card with no memory-junction sensor: the average is null, not 0.
        var a = Report("game.exe", 300, 65, memJunction: null);
        var b = Report("game.exe", 310, 67, memJunction: null);

        string markdown = SessionCompare.ToMarkdown(a, b);

        Assert.Contains("| Mem junction (°C) | — | — | — |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| Mem junction (°C) | 0.0 | 0.0 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmeasured_metric_has_no_delta_in_the_summary()
    {
        var a = Report("game.exe", power: null, gpuTemp: 65, memJunction: null);
        var b = Report("game.exe", power: null, gpuTemp: 67, memJunction: null);

        string text = SessionCompare.Describe(a, b);

        Assert.Contains("Board power: not measured", text, StringComparison.Ordinal);

        // FPS-per-watt is derived from power, so it must not appear at all.
        Assert.DoesNotContain("FPS per watt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparing_two_different_applications_warns_in_the_markdown_export()
    {
        // The on-screen summary always warned; the pasted form silently dropped
        // the caveat and shipped an incomparable FPS delta.
        string markdown = SessionCompare.ToMarkdown(
            Report("game-a.exe", 300, 65, 80),
            Report("game-b.exe", 310, 67, 82));

        Assert.Contains("Different applications", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_history_export_renders_absent_sensors_as_dashes()
    {
        string markdown = SessionReportStore.ToMarkdown([Report("game.exe", null, null, null)]);

        Assert.Contains("| — | — | — |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_logging_accepts_a_bare_filename()
    {
        // `monitor --csv session.csv` — the exact form the help text shows — used
        // to throw ArgumentException out of Main, because a bare name has no
        // directory part and Directory.CreateDirectory("") throws.
        string previous = Directory.GetCurrentDirectory();
        string scratch = Path.Combine(Path.GetTempPath(), $"afterglow-csv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            Directory.SetCurrentDirectory(scratch);

            using var logger = new CsvLogger("session.csv");
            logger.Start();
            logger.Log(new GpuSnapshot { Timestamp = DateTimeOffset.Now, DeviceIndex = 0 });

            Assert.NotNull(logger.CurrentFile);
            Assert.True(Path.IsPathRooted(logger.CurrentFile));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void An_undervolt_is_not_planned_for_a_gpu_with_no_core_offset_knob()
    {
        // The whole point of the plan is a core offset. On a GPU whose driver
        // exposes none, the plan used to be produced anyway, described in full
        // confidence, then silently clamped to nothing on apply.
        var recorder = new VfCurveRecorder();
        for (int i = 0; i < 200; i++)
        {
            recorder.Add(new GpuSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                DeviceIndex = 0,
                CoreClockMHz = 2600,
                CoreVoltageMv = 950,
            });
        }

        var noOffsetKnob = new TuningCapabilities { SupportsCoreOffset = false };

        Assert.Null(recorder.PlanUndervolt(950, 2800, currentOffsetMHz: 0, caps: noOffsetKnob));
    }

    [Fact]
    public void A_gpu_that_reports_no_core_voltage_is_reported_as_unmeasurable_not_as_still_collecting()
    {
        // Verified live on an Intel Arc B390: every telemetry read returns a null
        // core voltage. A V/F curve is voltage against clock, so it can never be
        // drawn there — but the surfaces said "Collecting…" forever, and the CLI
        // sampled for the full run (or locked the clock through an entire probe
        // sweep) to print "0 voltage points".
        var recorder = new VfCurveRecorder { DeviceIndex = 0 };
        for (int i = 0; i < 20; i++)
        {
            recorder.Add(new GpuSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                DeviceIndex = 0,
                CoreClockMHz = 2000,
                GpuUtilPct = 90,
                CoreVoltageMv = null,
            });
        }

        Assert.Equal(0, recorder.TotalSamples);
        Assert.Equal(20, recorder.SamplesMissingVoltage);
        Assert.True(recorder.VoltageSensorLooksAbsent);
    }

    [Fact]
    public void One_missed_voltage_read_is_a_glitch_not_a_missing_sensor()
    {
        // Voltage and clock come from separate driver calls, so a single null is
        // ordinary. Concluding "this GPU has no voltage sensor" from one would
        // refuse a probe that would have worked — the opposite error, and just as
        // wrong.
        var recorder = new VfCurveRecorder { DeviceIndex = 0 };
        recorder.Add(new GpuSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            DeviceIndex = 0,
            CoreClockMHz = 2000,
            GpuUtilPct = 90,
            CoreVoltageMv = null,
        });

        Assert.False(recorder.VoltageSensorLooksAbsent);

        // And a sensor that answers even once is never called absent, however
        // many reads missed around it.
        for (int i = 0; i < 30; i++)
        {
            recorder.Add(new GpuSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                DeviceIndex = 0,
                CoreClockMHz = 2000,
                GpuUtilPct = 90,
                CoreVoltageMv = i == 0 ? 900 : null,
            });
        }

        Assert.False(recorder.VoltageSensorLooksAbsent);
    }

    [Fact]
    public void Resetting_the_curve_does_not_invent_a_missing_voltage_sensor()
    {
        // Clear() reset the positive counter but not the negative one, so
        // "Reset curve" on a card that had just produced a curve flipped the
        // verdict to "this driver does not report core voltage" and refused the
        // probe on hardware that plainly does.
        var recorder = new VfCurveRecorder { DeviceIndex = 0 };
        for (int i = 0; i < 12; i++)
        {
            recorder.Add(new GpuSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                DeviceIndex = 0,
                CoreClockMHz = 2000,
                GpuUtilPct = 90,
                CoreVoltageMv = i % 2 == 0 ? 900 : null,
            });
        }

        recorder.Clear();

        Assert.False(recorder.VoltageSensorLooksAbsent);
        Assert.Equal(0, recorder.SamplesMissingVoltage);
        Assert.Equal(0, recorder.SamplesWithVoltage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unreadable_event_log_yields_an_unknown_cause_not_a_hard_reset_or_a_tuning_verdict(bool tuned)
    {
        // R-44: when the System log cannot be read, the evidence holds no crash
        // signature at all — yet the classifier fell through to its hard-reset
        // branches and asserted an instant power loss, naming the applied
        // offsets as the likeliest cause. An unclean app exit is all that is
        // actually known.
        var verdict = CrashClassifier.Classify(new CrashEvidence
        {
            SessionEnd = DateTimeOffset.UtcNow,
            EventLogAvailable = false,
            HeavyLoadAtDeath = true,
            SecondsSinceHeavyLoadEnded = 0,
            CoreOffsetMHz = tuned ? 200 : 0,
            MemOffsetMHz = tuned ? 1000 : 0,
        });

        Assert.NotNull(verdict);
        Assert.Equal(CrashCause.Unknown, verdict!.Cause);
        Assert.Contains("unknown", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reset", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lost power", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("likeliest cause", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("margin", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reduce the core offset", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_report_built_without_the_event_log_says_so_and_asserts_no_reset()
    {
        // The same case end to end: a tuned recording that died under load,
        // analysed with the event log unreadable (what QuerySystemLog returns
        // when EventLogReader throws).
        string[] lines =
        [
            "#flight v1 started=1000",
            "#1000 offsets core=200 mem=1000 lock=none vf=0 gpu=GPU-X",
            .. Enumerable.Range(2, 30).Select(s => FormattableString.Invariant($"{s * 1000}|2900|15001|70|85|450|99|1050|55")),
        ];
        var session = FlightSession.Parse(lines);
        var unreadable = new CrashForensics.EventEvidence(false, null, false, 0, 0, 0);

        var evidence = CrashForensics.BuildEvidence(session, session.EndedAt!.Value, unreadable);
        var verdict = CrashClassifier.Classify(evidence);
        Assert.NotNull(verdict);
        Assert.Equal(CrashCause.Unknown, verdict.Cause);

        string report = CrashForensics.BuildReport(session, evidence, unreadable, verdict, "GPU 0 — Fake");
        Assert.Contains("(event log could not be read)", report, StringComparison.Ordinal);
        Assert.Contains("Offsets applied: +200 MHz core, +1000 MHz memory", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Hard reset", report, StringComparison.Ordinal);
        Assert.DoesNotContain("lost power", report, StringComparison.Ordinal);
        Assert.DoesNotContain("likeliest cause", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_curve_measured_on_another_card_is_not_loaded_as_this_ones()
    {
        // A plain card swap made the new GPU inherit the old one's persisted
        // curve: the bounds check on load only rejects impossible numbers, and
        // another NVIDIA card's 700-1100 mV at 2000-3000 MHz passes it cleanly.
        // The foreign curve then rendered as this card's measurement and, on
        // NVIDIA, could be turned into a real offset-and-lock write.
        string path = Path.Combine(Path.GetTempPath(), $"afterglow-vf-identity-{Guid.NewGuid():N}.json");
        try
        {
            var written = new VfCurveRecorder { DeviceIndex = 0, GpuUuid = "GPU-AAA", VendorId = 0x10DE };
            for (int i = 0; i < 40; i++)
            {
                written.Add(new GpuSnapshot
                {
                    Timestamp = DateTimeOffset.Now,
                    DeviceIndex = 0,
                    CoreClockMHz = 2600,
                    GpuUtilPct = 90,
                    CoreVoltageMv = 950,
                });
            }

            written.Save(path);

            var sameCard = new VfCurveRecorder { DeviceIndex = 0, GpuUuid = "GPU-AAA", VendorId = 0x10DE };
            sameCard.Load(path);
            Assert.True(sameCard.TotalSamples > 0);

            var otherCard = new VfCurveRecorder { DeviceIndex = 0, GpuUuid = "GPU-BBB", VendorId = 0x10DE };
            otherCard.Load(path);
            Assert.Equal(0, otherCard.TotalSamples);
            Assert.Empty(otherCard.GetCurve());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

