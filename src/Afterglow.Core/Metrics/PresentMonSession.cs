using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Afterglow.Core.Diagnostics;

namespace Afterglow.Core.Metrics;

public enum PresentMonState
{
    NotStarted,
    Running,
    Exited,
    Failed,
}

public sealed record PresentEvent(
    int ProcessId,
    string Application,
    string SwapChain,
    double TimestampMs,
    double FrametimeMs,
    string PresentMode);

/// <summary>
/// Runs Intel's PresentMon console app (MIT-licensed, bundled under ThirdParty/PresentMon)
/// and streams present events parsed from its stdout CSV. Column positions are resolved
/// from the header line at runtime so minor PresentMon schema changes don't break parsing.
/// Each capture runs under an ETW session name of its own, so captures in different
/// processes (the GUI and a CLI fps run) never stop one another; sessions orphaned by a
/// crashed host are stopped on the next start. ETW present tracing requires elevation
/// (or Performance Log Users membership) — start failures surface through
/// <see cref="State"/> and <see cref="FailureReason"/>.
/// </summary>
public sealed class PresentMonSession : IDisposable
{
    /// <summary>
    /// Every capture's ETW session is named <c>Afterglow-{host pid}-{capture number}</c>
    /// (R-29). All captures used to share "Afterglow" and start with
    /// --stop_existing_session, so starting a CLI fps run stopped the GUI's trace.
    /// The host pid in the name is what lets a later start tell an orphan (its host
    /// is gone) from another capture that is still running.
    /// </summary>
    public const string SessionNamePrefix = "Afterglow-";

    public const string BundledExeName = "PresentMon-2.5.1-x64.exe";

    private static int s_captureCount;

    /// <summary>The session names of this process's running captures; the orphan sweep never stops one.</summary>
    private static readonly ConcurrentDictionary<string, byte> s_ownSessions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _lock = new();

    /// <summary>
    /// Serializes line handling against the start and stop of a capture (R-26). A
    /// line is checked against the current generation, parsed and raised under it,
    /// and the generation moves on under it; so once <see cref="Stop"/> or
    /// <see cref="Start"/> returns, no line from an earlier PresentMon is parsed
    /// against the new header or raised into the new capture's windows. Lock order:
    /// <c>_lock</c>, then this. <see cref="FramePresented"/> handlers run under it
    /// and must not call <see cref="Start"/> or <see cref="Stop"/>.
    /// </summary>
    private readonly Lock _lineLock = new();

    private Process? _process;
    private string? _sessionName;
    private int _generation;
    private int _headerParsed;
    private int _colApplication = -1;
    private int _colProcessId = -1;
    private int _colSwapChain = -1;
    private int _colTimeSeconds = -1;
    private int _colBetweenPresents = -1;
    private int _colPresentMode = -1;
    private long _parseErrors;
    private long _totalLines;
    private double _timeToMs = 1000.0; // TimeInSeconds → ms; 1.0 when the column is TimeInMs
    private readonly List<string> _firstLines = [];

    public static string DefaultExePath => Path.Combine(
        AppContext.BaseDirectory, "ThirdParty", "PresentMon", BundledExeName);

    public PresentMonState State { get; private set; } = PresentMonState.NotStarted;

    public string? FailureReason { get; private set; }

    /// <summary>The ETW session name of the running capture; null when none is running.</summary>
    public string? SessionName
    {
        get
        {
            lock (_lock)
            {
                return _sessionName;
            }
        }
    }

    public long ParseErrors => Interlocked.Read(ref _parseErrors);

    /// <summary>Raw stdout lines received (incl. header) — diagnostic.</summary>
    public long TotalLines => Interlocked.Read(ref _totalLines);

    /// <summary>First few raw lines, for diagnosing schema changes.</summary>
    public IReadOnlyList<string> FirstLines
    {
        get
        {
            lock (_firstLines)
            {
                return _firstLines.ToArray();
            }
        }
    }

    /// <summary>True once the CSV header has been recognized.</summary>
    public bool HeaderParsed => Volatile.Read(ref _headerParsed) != 0;

    /// <summary>Raised for every present, on PresentMon's output thread.</summary>
    public event Action<PresentEvent>? FramePresented;

    /// <summary>Raised when the capture process exits or fails.</summary>
    public event Action<PresentMonState, string?>? StateChanged;

    /// <summary>Starts capture for all processes. Returns false (with reason) when it cannot start.</summary>
    public bool Start(string? exePath = null)
    {
        lock (_lock)
        {
            if (State == PresentMonState.Running)
            {
                return true;
            }

            // A PresentMon left from a capture that failed while it ran (its stderr
            // refusal arrives with the process still up) is stopped, not leaked.
            StopCore();
            int generation = BeginGeneration();

            exePath ??= DefaultExePath;
            if (!File.Exists(exePath))
            {
                SetState(PresentMonState.Failed,
                    $"PresentMon binary not found at '{exePath}'. FPS metrics are disabled. " +
                    "Reinstall Afterglow or place the official Intel PresentMon console executable there.");
                return false;
            }

            StopOrphanedSessions();

            string sessionName = NewSessionName();
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = BuildArguments(sessionName),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data, generation);
            process.ErrorDataReceived += (_, e) => OnErrorLine(e.Data, generation);
            process.Exited += (_, _) => OnExited(generation);
            s_ownSessions.TryAdd(sessionName, 0);
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                _process = process;
                _sessionName = sessionName;
                StopCore();
                SetState(PresentMonState.Failed, $"Could not start PresentMon: {ex.Message}");
                return false;
            }

            _process = process;
            _sessionName = sessionName;
            SetState(PresentMonState.Running, null);
            return true;
        }
    }

    /// <summary>The ETW session name for the next capture: unique per host process and per capture.</summary>
    internal static string NewSessionName() => string.Create(CultureInfo.InvariantCulture,
        $"{SessionNamePrefix}{Environment.ProcessId}-{Interlocked.Increment(ref s_captureCount)}");

    internal static string BuildArguments(string sessionName) => string.Join(' ',
        "--session_name", sessionName,
        // With a unique name this only reclaims an orphan of this exact name — a
        // session a crashed host with this same (reused) pid left behind.
        "--stop_existing_session",
        "--output_stdout",
        "--no_console_stats",
        "--no_track_input");

    /// <summary>The host pid encoded in a session name of this app's form; false for any other name.</summary>
    internal static bool TryParseHostPid(string sessionName, out int hostPid)
    {
        hostPid = 0;
        if (!sessionName.StartsWith(SessionNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = sessionName.AsSpan(SessionNamePrefix.Length);
        int dash = rest.IndexOf('-');
        return dash > 0
            && int.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out hostPid)
            && int.TryParse(rest[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Whether a running trace session is an orphaned capture of this app: named in
    /// this app's form, and either its host process is gone, or the host is this
    /// process and none of its running captures owns the name. The legacy shared
    /// "Afterglow" name and every other tool's sessions are never orphans here.
    /// </summary>
    internal static bool IsOrphaned(string sessionName, int ownPid, Func<string, bool> ownsSession, Func<int, bool> isRunning)
    {
        if (!TryParseHostPid(sessionName, out int hostPid))
        {
            return false;
        }

        return hostPid == ownPid ? !ownsSession(sessionName) : !isRunning(hostPid);
    }

    /// <summary>
    /// Stops the sessions captures left behind when their host died without
    /// stopping them — a crashed GUI, a CLI killed mid-capture. The shared name
    /// used to reclaim those on the next start; a session whose host is still
    /// running is another capture in progress and is left alone (R-29).
    /// </summary>
    private static void StopOrphanedSessions()
    {
        foreach (string name in EtwSessions.RunningSessionNames())
        {
            if (IsOrphaned(name, Environment.ProcessId, s_ownSessions.ContainsKey, static pid => ProcessLiveness.IsRunning(pid)))
            {
                var outcome = EtwSessions.Stop(name, out int error);
                Log.Info($"Orphaned PresentMon trace session '{name}': {outcome}" +
                         (outcome == EtwSessions.StopOutcome.Failed ? $" (error {error})" : string.Empty));
            }
        }
    }

    /// <summary>Caller holds <c>_lock</c>. A fresh capture: new generation, header and diagnostics reset.</summary>
    private int BeginGeneration()
    {
        lock (_lineLock)
        {
            _generation++;
            Volatile.Write(ref _headerParsed, 0);
            _colApplication = -1;
            _colProcessId = -1;
            _colSwapChain = -1;
            _colTimeSeconds = -1;
            _colBetweenPresents = -1;
            _colPresentMode = -1;
            _timeToMs = 1000.0;
            Interlocked.Exchange(ref _parseErrors, 0);
            Interlocked.Exchange(ref _totalLines, 0);
            lock (_firstLines)
            {
                _firstLines.Clear();
            }

            return _generation;
        }
    }

    /// <summary>The current capture's generation (test seam).</summary>
    internal int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Test seam: one stdout line, as if the capture of <paramref name="generation"/>
    /// (default: the current one) printed it.
    /// </summary>
    internal void ProcessLine(string line, int? generation = null) => OnLine(line, generation ?? Generation);

    private void OnLine(string? line, int generation)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_lineLock)
        {
            if (generation != _generation)
            {
                return; // a stopped capture's late output
            }

            ParseLine(line);
        }
    }

    /// <summary>Caller holds <c>_lineLock</c>.</summary>
    private void ParseLine(string line)
    {
        Interlocked.Increment(ref _totalLines);
        lock (_firstLines)
        {
            if (_firstLines.Count < 3)
            {
                _firstLines.Add(line.Length > 300 ? line[..300] : line);
            }
        }

        if (Volatile.Read(ref _headerParsed) == 0)
        {
            if (TryParseHeader(line))
            {
                Volatile.Write(ref _headerParsed, 1);
            }

            return;
        }

        string[] fields = line.Split(',');
        int maxCol = Math.Max(Math.Max(_colApplication, _colProcessId),
            Math.Max(Math.Max(_colSwapChain, _colTimeSeconds), _colBetweenPresents));
        if (fields.Length <= maxCol)
        {
            Interlocked.Increment(ref _parseErrors);
            return;
        }

        if (!int.TryParse(fields[_colProcessId], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ||
            !double.TryParse(fields[_colTimeSeconds], NumberStyles.Float, CultureInfo.InvariantCulture, out double timeSeconds) ||
            !double.TryParse(fields[_colBetweenPresents], NumberStyles.Float, CultureInfo.InvariantCulture, out double frametime))
        {
            Interlocked.Increment(ref _parseErrors);
            return;
        }

        string presentMode = _colPresentMode >= 0 && _colPresentMode < fields.Length
            ? fields[_colPresentMode]
            : string.Empty;

        FramePresented?.Invoke(new PresentEvent(
            pid,
            fields[_colApplication],
            fields[_colSwapChain],
            timeSeconds * _timeToMs,
            frametime,
            presentMode));
    }

    private bool TryParseHeader(string line)
    {
        string[] columns = line.Split(',');
        for (int i = 0; i < columns.Length; i++)
        {
            // Trim whitespace plus a possible UTF-8 BOM on the first column.
            switch (columns[i].Trim().TrimStart('﻿'))
            {
                case "Application":
                    _colApplication = i;
                    break;
                case "ProcessID":
                    _colProcessId = i;
                    break;
                case "SwapChainAddress":
                    _colSwapChain = i;
                    break;
                case "TimeInSeconds":
                    _colTimeSeconds = i;
                    _timeToMs = 1000.0;
                    break;
                case "TimeInMs":
                    _colTimeSeconds = i;
                    _timeToMs = 1.0;
                    break;
                case "MsBetweenPresents" or "msBetweenPresents":
                    _colBetweenPresents = i;
                    break;
                case "PresentMode":
                    _colPresentMode = i;
                    break;
                default:
                    break;
            }
        }

        return _colApplication >= 0 && _colProcessId >= 0 && _colSwapChain >= 0
            && _colTimeSeconds >= 0 && _colBetweenPresents >= 0;
    }

    private void OnErrorLine(string? line, int generation)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed to start trace", StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                SetState(PresentMonState.Failed,
                    "ETW trace session was refused (run elevated, or add your account to the " +
                    $"'Performance Log Users' group). PresentMon said: {line.Trim()}");
            }
        }
    }

    private void OnExited(int generation)
    {
        lock (_lock)
        {
            // A capture that was stopped or replaced: its exit is not news.
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            if (State == PresentMonState.Running)
            {
                SetState(PresentMonState.Exited, "PresentMon exited unexpectedly.");
            }
        }
    }

    private void SetState(PresentMonState state, string? reason)
    {
        State = state;
        FailureReason = reason;
        StateChanged?.Invoke(state, reason);
    }

    /// <summary>
    /// Ends the capture: PresentMon is stopped and its ETW session with it. The
    /// session object can start a new capture afterwards.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    /// <summary>Caller holds <c>_lock</c>.</summary>
    private void StopCore()
    {
        // From here on nothing the stopped PresentMon still has in flight is parsed
        // or raised (R-26), and its exit is not reported as a failure.
        lock (_lineLock)
        {
            _generation++;
        }

        if (_process is { } process)
        {
            _process = null;
            // Plain Kill (direct TerminateProcess). The entireProcessTree variant's
            // child enumeration has been observed to hang against this elevated
            // child; PresentMon spawns no children.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    _ = process.WaitForExit(2000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.ComponentModel.Win32Exception or SystemException)
            {
            }

            process.Dispose();
        }

        if (_sessionName is { } name)
        {
            _sessionName = null;

            // A killed PresentMon leaves its ETW session running with no consumer.
            // Under the shared name the next start's --stop_existing_session
            // reclaimed it; a name of its own has to be stopped by name (R-29).
            // Should that fail, the name is no longer this process's, so the next
            // start's orphan sweep retries it.
            var outcome = EtwSessions.Stop(name, out int error);
            if (outcome == EtwSessions.StopOutcome.Failed)
            {
                Log.Warn($"PresentMon trace session '{name}' could not be stopped (error {error}).");
            }

            s_ownSessions.TryRemove(name, out _);
        }

        if (State == PresentMonState.Running)
        {
            State = PresentMonState.Exited;
        }
    }
}
