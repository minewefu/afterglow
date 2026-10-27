using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Afterglow.Core.Metrics;

namespace Afterglow.Core.Tests;

/// <summary>
/// The PresentMon session's own contract: one ETW session name per capture
/// (R-29), and no output of a stopped capture leaking into the next (R-26).
/// No PresentMon is started; the only ETW calls made are a read-only listing
/// and a stop of a name that does not exist.
/// </summary>
public class PresentMonSessionTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentMode,TimeInSeconds,MsBetweenPresents";

    private static readonly string NoPresentMon =
        Path.Combine(Path.GetTempPath(), $"afterglow-no-presentmon-{Guid.NewGuid():N}.exe");

    private static string Frame(int pid, double timeS) =>
        string.Create(CultureInfo.InvariantCulture, $"game.exe,{pid},0xA,Hardware: Independent Flip,{timeS:F6},16.666667");

    [Fact]
    public void Every_capture_runs_under_its_own_session_name_that_names_its_host()
    {
        // All captures used to share "Afterglow" and start with
        // --stop_existing_session, so a CLI fps run stopped the GUI's trace.
        string first = PresentMonSession.NewSessionName();
        string second = PresentMonSession.NewSessionName();

        Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
        Assert.True(PresentMonSession.TryParseHostPid(first, out int host));
        Assert.Equal(Environment.ProcessId, host);

        string args = PresentMonSession.BuildArguments(first);
        Assert.Contains($"--session_name {first} ", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--session_name Afterglow ", args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Afterglow-4100-3", 4100)]
    [InlineData("afterglow-7-1", 7)]
    public void A_session_name_of_this_apps_form_names_its_host(string name, int expectedHost)
    {
        Assert.True(PresentMonSession.TryParseHostPid(name, out int host));
        Assert.Equal(expectedHost, host);
    }

    [Theory]
    [InlineData("Afterglow")]            // the legacy shared name: an older version may be capturing under it
    [InlineData("PresentMon")]           // PresentMon's default, someone else's capture
    [InlineData("Afterglow-")]
    [InlineData("Afterglow-12")]
    [InlineData("Afterglow-x-1")]
    [InlineData("Afterglow-12-x")]
    [InlineData("Afterglow-+12-1")]
    [InlineData("EventLog-System")]
    public void Any_other_session_name_is_never_this_apps(string name)
    {
        Assert.False(PresentMonSession.TryParseHostPid(name, out _));
        Assert.False(PresentMonSession.IsOrphaned(name, ownPid: 1, ownsSession: _ => false, isRunning: _ => false));
    }

    [Fact]
    public void Only_a_session_whose_host_is_gone_or_disowned_is_an_orphan()
    {
        const int Own = 500;

        // Another host still running: a concurrent capture (the GUI while the
        // CLI starts, or the reverse). Stopping it is the bug being fixed.
        Assert.False(PresentMonSession.IsOrphaned("Afterglow-900-1", Own, _ => false, isRunning: pid => pid == 900));

        // Its host is gone: a crashed GUI or a killed CLI left it behind.
        Assert.True(PresentMonSession.IsOrphaned("Afterglow-900-1", Own, _ => false, isRunning: _ => false));

        // This process: orphaned only when none of its running captures owns it.
        Assert.False(PresentMonSession.IsOrphaned("Afterglow-500-2", Own, ownsSession: _ => true, isRunning: _ => true));
        Assert.True(PresentMonSession.IsOrphaned("Afterglow-500-2", Own, ownsSession: _ => false, isRunning: _ => true));
    }

    [Fact]
    public void The_trace_properties_block_matches_evntrace_h()
    {
        // EVENT_TRACE_PROPERTIES on x64; ETW reads these offsets to find the
        // name buffers, and a drift would write a session name into garbage.
        Assert.Equal(120, Unsafe.SizeOf<EtwSessions.EventTraceProperties>());
        Assert.Equal(48, (int)Marshal.OffsetOf<EtwSessions.EventTraceProperties>(nameof(EtwSessions.EventTraceProperties.BufferSize)));
        Assert.Equal(104, (int)Marshal.OffsetOf<EtwSessions.EventTraceProperties>(nameof(EtwSessions.EventTraceProperties.LoggerThreadId)));
        Assert.Equal(112, (int)Marshal.OffsetOf<EtwSessions.EventTraceProperties>(nameof(EtwSessions.EventTraceProperties.LogFileNameOffset)));
        Assert.Equal(116, (int)Marshal.OffsetOf<EtwSessions.EventTraceProperties>(nameof(EtwSessions.EventTraceProperties.LoggerNameOffset)));
    }

    [Fact]
    public void The_etw_calls_answer_without_touching_any_running_session()
    {
        // Read-only listing, and a stop of a name nothing runs under.
        var names = EtwSessions.RunningSessionNames();
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));

        string nobody = $"AfterglowTest-{Guid.NewGuid():N}";
        Assert.NotEqual(EtwSessions.StopOutcome.Stopped, EtwSessions.Stop(nobody, out _));
    }

    /// <summary>
    /// R-26. A stopped PresentMon's last lines can still be in flight on its
    /// output thread; they must not land in the next capture's windows or be
    /// parsed against its header.
    /// </summary>
    [Fact]
    public void Output_of_a_stopped_capture_is_never_raised_or_parsed()
    {
        using var session = new PresentMonSession();
        int raised = 0;
        session.FramePresented += _ => raised++;

        session.ProcessLine(Header);
        session.ProcessLine(Frame(42, 1.0));
        Assert.Equal(1, raised);
        int stoppedCapture = session.Generation;

        session.Stop();
        session.ProcessLine(Frame(42, 2.0), stoppedCapture);
        Assert.Equal(1, raised);

        // A new capture starts from a fresh header and fresh diagnostics.
        Assert.False(session.Start(NoPresentMon));
        Assert.False(session.HeaderParsed);
        Assert.Equal(0, session.TotalLines);
        session.ProcessLine(Frame(42, 3.0), stoppedCapture);
        Assert.Equal(1, raised);
        Assert.Equal(0, session.TotalLines);
    }

    [Fact]
    public void Process_liveness_tells_a_running_process_from_a_gone_or_reused_id()
    {
        using var self = Process.GetCurrentProcess();
        var started = self.StartTime.ToUniversalTime();

        Assert.True(ProcessLiveness.IsRunning(Environment.ProcessId));
        Assert.True(ProcessLiveness.IsRunning(Environment.ProcessId, DateTime.UtcNow));

        // Seen before this process started: this process is a reuse of the id.
        Assert.False(ProcessLiveness.IsRunning(Environment.ProcessId, started.AddMinutes(-1)));

        Assert.False(ProcessLiveness.IsRunning(0));
        Assert.False(ProcessLiveness.IsRunning(-5));

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        Assert.True(child.WaitForExit(10_000));
        Assert.False(ProcessLiveness.IsRunning(child.Id));
    }
}
