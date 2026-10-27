using Afterglow.Core.Diagnostics;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

public sealed class FlightRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-flight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            foreach (string file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    /// <summary>Reads a file the recorder still holds open for writing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static GpuSnapshot Snap(long ms, uint util = 10) => new()
    {
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms),
        DeviceIndex = 0,
        CoreClockMHz = 2500,
        GpuTempC = 50,
        PowerW = 100,
        GpuUtilPct = util,
    };

    [Fact]
    public void A_disposed_session_ends_with_the_clean_marker_and_reads_as_clean()
    {
        using (var recorder = new FlightRecorder(_dir))
        {
            recorder.RecordOffsets(100, 500);
            recorder.Record(Snap(1_000));
            recorder.Record(Snap(2_000));
        }

        string[] lines = File.ReadAllLines(PathOf("current.log"));
        Assert.StartsWith("#flight v1 started=", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" clean-shutdown", lines[^1], StringComparison.Ordinal);
        Assert.True(FlightSession.EndedCleanly(PathOf("current.log")));
        Assert.Null(CrashForensics.AnalyzePreviousSession(_dir));
    }

    [Fact]
    public void A_segment_roll_that_cannot_recreate_the_file_stops_recording_instead_of_crashing()
    {
        // The writer runs on a background thread, where an unhandled exception
        // would end the process before the fans are handed back. Make the roll's
        // re-create fail: rotation blocked (a directory where the older segment
        // goes) and current.log read-only, so FileMode.Create is denied.
        using (var recorder = new FlightRecorder(_dir, segmentBytes: 1_000))
        {
            Directory.CreateDirectory(PathOf("current.older.log"));
            recorder.Marker("before");
            SpinWait.SpinUntil(() => ReadShared(PathOf("current.log")).Contains("before", StringComparison.Ordinal), 2000);
            File.SetAttributes(PathOf("current.log"), FileAttributes.ReadOnly);

            for (int i = 0; i < 100; i++)
            {
                recorder.Record(Snap(1_000 + (i * 1_000L)));
            }
        }

        // Still here (the test process survived), and the stopped session wrote nothing more.
        Assert.DoesNotContain(File.ReadAllLines(PathOf("current.log")), l => l.EndsWith(" clean-shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_session_moves_the_last_one_to_previous()
    {
        using (var first = new FlightRecorder(_dir))
        {
            first.Marker("first-session");
        }

        using (var second = new FlightRecorder(_dir))
        {
            second.Marker("second-session");
        }

        Assert.Contains(File.ReadAllLines(PathOf("previous.log")), l => l.EndsWith(" first-session", StringComparison.Ordinal));
        Assert.Contains(File.ReadAllLines(PathOf("current.log")), l => l.EndsWith(" second-session", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_session_rolls_over_and_restates_the_applied_offsets()
    {
        using (var recorder = new FlightRecorder(_dir, segmentBytes: 2_000))
        {
            recorder.RecordOffsets(75, 2000);
            for (int i = 0; i < 200; i++)
            {
                recorder.Record(Snap(1_000 + (i * 1_000L)));
            }
        }

        // Bounded: the older segment holds the session's start, current.log its end.
        Assert.True(new FileInfo(PathOf("current.log")).Length < 4_000);
        Assert.True(File.Exists(PathOf("current.older.log")));

        // The final segment alone still knows what was applied.
        var tail = FlightSession.Parse(File.ReadAllLines(PathOf("current.log")));
        Assert.Equal(75, tail.CoreOffsetMHz);
        Assert.Equal(2000, tail.MemOffsetMHz);
        Assert.True(tail.CleanShutdown);
    }

    [Fact]
    public void A_roll_over_restates_the_whole_tuning_line_not_just_the_offsets()
    {
        // R-43 widened the tuning line; the carry-over at a segment roll must
        // carry the lock, the V/F edits and the card identity with it, or a
        // crash ten hours in would lose them exactly as it once lost the offsets.
        using (var recorder = new FlightRecorder(_dir, segmentBytes: 2_000))
        {
            recorder.RecordTuning(new FlightTuning
            {
                CoreOffsetMHz = 0,
                MemOffsetMHz = 0,
                LockRecorded = true,
                LockedCoreClockMHz = 1800,
                VfPointsEdited = 3,
                VfPointMaxOffsetMHz = 120,
                GpuId = "GPU-ROLL",
            });
            for (int i = 0; i < 200; i++)
            {
                recorder.Record(Snap(1_000 + (i * 1_000L)));
            }
        }

        Assert.True(File.Exists(PathOf("current.older.log")));
        var tail = FlightSession.Parse(File.ReadAllLines(PathOf("current.log")));
        Assert.Equal(1800u, tail.Tuning?.LockedCoreClockMHz);
        Assert.Equal(3, tail.Tuning?.VfPointsEdited);
        Assert.Equal(120, tail.Tuning?.VfPointMaxOffsetMHz);
        Assert.Equal("GPU-ROLL", tail.Tuning?.GpuId);
    }

    [Fact]
    public void The_tuning_line_is_still_an_offsets_line_to_older_readers()
    {
        // Every reader before R-43 split the marker on spaces and read only
        // core= and mem=, keeping the last value of each. The widened line has
        // to give such a reader the same two numbers — so it keeps the old
        // prefix, and nothing it adds can be mistaken for either field.
        string text = new FlightTuning
        {
            CoreOffsetMHz = -25,
            MemOffsetMHz = 800,
            LockRecorded = true,
            LockedCoreClockMHz = null,
            VfPointsEdited = 2,
            VfPointMaxOffsetMHz = 90,
            GpuId = "NVIDIA core=999 mem=999\nGPU",   // hostile identity: spaces, fake fields, a newline
        }.ToMarkerText();

        Assert.StartsWith("offsets core=-25 mem=800 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text);
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(parts, p => p.StartsWith("core=", StringComparison.Ordinal));
        Assert.Single(parts, p => p.StartsWith("mem=", StringComparison.Ordinal));

        var parsed = FlightTuning.Parse(text);
        Assert.Equal(-25, parsed.CoreOffsetMHz);
        Assert.Equal(800, parsed.MemOffsetMHz);
        Assert.True(parsed.LockRecorded);
        Assert.Null(parsed.LockedCoreClockMHz);
        Assert.Equal("NVIDIA_core=999_mem=999_GPU", parsed.GpuId);
    }

    [Fact]
    public void A_recording_from_before_the_tuning_line_reads_its_lock_and_curve_as_not_recorded()
    {
        // Existing recordings stay readable — and honest: their offsets are
        // known, but "no lock field" means the lock was never recorded, not
        // that there was none.
        var session = FlightSession.Parse(["#flight v1 started=1", "#1000 offsets core=0 mem=0", "2000|300|405|40|40|30|2|700|30"]);

        Assert.NotNull(session.Tuning);
        Assert.False(session.Tuning.LockRecorded);
        Assert.Null(session.Tuning.VfPointsEdited);
        Assert.False(session.Tuning.FullyRecorded);

        var evidence = CrashForensics.BuildEvidence(
            session, session.EndedAt!.Value, new CrashForensics.EventEvidence(true, 0, true, 0, 0, 0));
        Assert.False(evidence.TuningFullyRecorded);

        var verdict = CrashClassifier.Classify(evidence);
        Assert.NotNull(verdict);
        Assert.DoesNotContain("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_later_tuning_line_does_not_inherit_an_earlier_lock()
    {
        var session = FlightSession.Parse(
        [
            "#1000 offsets core=0 mem=0 lock=1500 vf=0 gpu=GPU-A",
            "#2000 offsets core=0 mem=0 lock=none vf=0 gpu=GPU-A",
        ]);

        Assert.True(session.Tuning?.LockRecorded);
        Assert.Null(session.Tuning?.LockedCoreClockMHz);
        Assert.False(session.Tuning?.AnyApplied);
    }

    [Fact]
    public void An_older_segment_from_a_session_that_is_not_the_previous_one_is_dropped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("previous.older.log"), "stale");
        File.WriteAllText(PathOf("current.log"), "#flight v1 started=1\n");

        using (new FlightRecorder(_dir))
        {
        }

        Assert.False(File.Exists(PathOf("previous.older.log")));
        Assert.True(File.Exists(PathOf("previous.log")));
    }

    [Theory]
    [InlineData("#flight v1 started=1\n1000|2500|810|50||100|10||\n")]           // died mid-flight
    [InlineData("#flight v1 started=1\n#1000 clean-shutdown\n1000|25")]           // torn line after a marker
    [InlineData("")]
    public void Unfinished_sessions_are_not_mistaken_for_clean_ones(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("current.log"), content);
        Assert.False(FlightSession.EndedCleanly(PathOf("current.log")));
    }

    [Theory]
    [InlineData("#9000000000000000 boom")]                        // marker beyond year 9999
    [InlineData("9999999999999999|900|1500|60|70|300|99|900|40")] // sample beyond year 9999
    [InlineData("-99999999999999999|1|1|1|1|1|1")]
    public void Out_of_range_timestamps_are_skipped_not_thrown(string poisoned)
    {
        // F-34: a valid Int64 that DateTimeOffset rejects used to escape startup
        // and, since the file was never rotated, block every later launch.
        var session = FlightSession.Parse(["#flight v1 started=1", "#1000 offsets core=50 mem=0", poisoned]);

        Assert.Equal(50, session.CoreOffsetMHz);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), session.EndedAt);
    }

    [Fact]
    public void A_poisoned_recording_does_not_stop_the_analysis()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(PathOf("current.log"), ["#flight v1 started=1", "#9000000000000000 boom"]);

        // No throw; a recording with nothing usable in it yields no report.
        Assert.Null(CrashForensics.AnalyzePreviousSession(_dir));
    }

    [Fact]
    public void A_huge_legacy_recording_falls_back_to_a_full_read_for_its_offsets()
    {
        // Pre-segment recordings stated offsets once, at the start; a tail-only
        // read of a multi-day file would report stock clocks for a crash at +100/+500.
        Directory.CreateDirectory(_dir);
        string path = PathOf("current.log");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("#flight v1 started=1");
            writer.WriteLine("#2 offsets core=100 mem=500");
            string filler = new('x', 60);
            long bytes = 0;
            for (long ms = 1_000; bytes < FlightSession.TailBytes + 100_000; ms += 1_000)
            {
                string line = $"{ms}|2500|810|50||100|10||{filler}";
                writer.WriteLine(line);
                bytes += line.Length + 2;
            }
        }

        var session = FlightSession.Load(path);

        Assert.NotNull(session);
        Assert.Equal(100, session.CoreOffsetMHz);
        Assert.Equal(500, session.MemOffsetMHz);
        Assert.False(session.CleanShutdown);
    }

    [Fact]
    public void A_recording_whose_tail_names_the_offsets_is_read_from_the_tail_only()
    {
        Directory.CreateDirectory(_dir);
        string path = PathOf("current.log");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("#flight v1 started=1");
            writer.WriteLine("#2 offsets core=999 mem=999");
            string filler = new('x', 60);
            long bytes = 0;
            long ms = 1_000;
            for (; bytes < FlightSession.TailBytes + 100_000; ms += 1_000)
            {
                string line = $"{ms}|2500|810|50||100|10||{filler}";
                writer.WriteLine(line);
                bytes += line.Length + 2;
            }

            writer.WriteLine($"#{ms} offsets core=100 mem=500");
        }

        var session = FlightSession.Load(path);

        Assert.NotNull(session);
        Assert.Equal(100, session.CoreOffsetMHz);

        // The head of the file (and its stale 999 marker) was never parsed.
        Assert.DoesNotContain(session.Markers, m => m.Contains("999", StringComparison.Ordinal));
    }
}
