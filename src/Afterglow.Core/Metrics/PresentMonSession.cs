using System.Diagnostics;
using System.Globalization;

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
/// The capture uses a dedicated ETW session name and stops any orphan from a crashed
/// previous run. ETW present tracing requires elevation (or Performance Log Users
/// membership) — start failures surface through <see cref="State"/> and <see cref="FailureReason"/>.
/// </summary>
public sealed class PresentMonSession : IDisposable
{
    public const string SessionName = "Afterglow";
    public const string BundledExeName = "PresentMon-2.5.1-x64.exe";

    private readonly object _lock = new();
    private Process? _process;
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

    /// <summary>Raised for every present, on a threadpool thread.</summary>
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

            exePath ??= DefaultExePath;
            if (!File.Exists(exePath))
            {
                SetState(PresentMonState.Failed,
                    $"PresentMon binary not found at '{exePath}'. FPS metrics are disabled. " +
                    "Reinstall Afterglow or place the official Intel PresentMon console executable there.");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(' ',
                    "--session_name", SessionName,
                    "--stop_existing_session",
                    "--output_stdout",
                    "--no_console_stats",
                    "--no_track_input"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _headerParsed = 0;
                _process.OutputDataReceived += (_, e) => OnLine(e.Data);
                _process.ErrorDataReceived += (_, e) => OnErrorLine(e.Data);
                _process.Exited += (_, _) => OnExited();
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                SetState(PresentMonState.Failed, $"Could not start PresentMon: {ex.Message}");
                return false;
            }

            SetState(PresentMonState.Running, null);
            return true;
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

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
            switch (columns[i].Trim().TrimStart('\uFEFF'))
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

    private void OnErrorLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed to start trace", StringComparison.OrdinalIgnoreCase))
        {
            SetState(PresentMonState.Failed,
                "ETW trace session was refused (run elevated, or add your account to the " +
                $"'Performance Log Users' group). PresentMon said: {line.Trim()}");
        }
    }

    private void OnExited()
    {
        lock (_lock)
        {
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

    public void Dispose()
    {
        lock (_lock)
        {
            if (_process is { } process)
            {
                _process = null;
                // Plain Kill (direct TerminateProcess). The entireProcessTree variant's
                // child enumeration has been observed to hang against this elevated
                // child; PresentMon spawns no children, and the ETW session is
                // reclaimed by --stop_existing_session on the next start regardless.
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

            if (State == PresentMonState.Running)
            {
                State = PresentMonState.Exited;
            }
        }
    }
}
