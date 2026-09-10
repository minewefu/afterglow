using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Afterglow.App.Services;
using Afterglow.App.ViewModels;
using Afterglow.Core.Tuning;

namespace Afterglow.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application lifetime: fields are disposed in OnExit.")]
public partial class App : Application
{
    private AppServices? _services;

    /// <summary>
    /// --demo / --screenshot: a throwaway process that starts the services but
    /// applies nothing to real hardware, and must not touch the applied-state
    /// record the resident instance owns.
    /// </summary>
    private bool _ephemeralRun;
    private MainViewModel? _mainViewModel;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private DispatcherTimer? _tooltipTimer;
    private bool _exitRequested;
    private System.Threading.Mutex? _singleInstanceMutex;
    private System.Threading.EventWaitHandle? _activationSignal;

    public static new App Current => (App)Application.Current;

    public AppServices Services => _services ?? throw new InvalidOperationException("Services not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = new HashSet<string>(e.Args, StringComparer.OrdinalIgnoreCase);

        if (args.Contains("--present-storm"))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new PresentStormWindow().Show();
            return;
        }

        // Installer hook: register the elevated no-UAC logon task and exit.
        // The installer's post-install step runs elevated, so this succeeds
        // without any prompt of its own.
        if (args.Contains("--register-startup"))
        {
            string? failure = AppServices.CheckElevated()
                ? StartupTaskService.Enable().Error
                : "the post-install step did not run elevated.";
            if (failure is not null)
            {
                // Inno runs this hidden and ignores the exit code, so the log is
                // the only place a refusal can still be read afterwards.
                Core.Diagnostics.Log.Error($"--register-startup did not register the task: {failure}");
            }

            Shutdown(failure is null ? 0 : 1);
            return;
        }

        bool demo = args.Contains("--demo");
        string? screenshotPath = GetArgValue(e.Args, "--screenshot");
        _ephemeralRun = demo || screenshotPath is not null;
        string? page = GetArgValue(e.Args, "--page");

        // Single instance: two Afterglows would fight over fan control, hotkeys,
        // and the TDR watchdog. A second launch signals the first to come forward.
        if (screenshotPath is null && !demo)
        {
            _singleInstanceMutex = new System.Threading.Mutex(
                initiallyOwned: true, @"Local\AfterglowSingleInstance", out bool isFirst);
            _activationSignal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, @"Local\AfterglowActivate");
            if (!isFirst)
            {
                _ = _activationSignal.Set();
                Shutdown();
                return;
            }
        }

        // Self-elevate unless running a headless/degraded scenario. Declining the
        // prompt continues in monitoring-only mode.
        if (!demo && screenshotPath is null && !args.Contains("--no-elevate") && !AppServices.CheckElevated())
        {
            if (TryRelaunchElevated(e.Args))
            {
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            // Screenshot runs are ephemeral: no black box (it belongs to the
            // resident instance, which holds the flight file).
            _services = AppServices.Create(demo, enableBlackBox: screenshotPath is null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Afterglow could not start its hardware services:\n\n{ex}",
                "Afterglow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _mainViewModel = new MainViewModel(_services);
        if (page is not null)
        {
            _mainViewModel.NavigateTo(page);
        }

        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;

        if (screenshotPath is not null)
        {
            RunScreenshotMode(window, screenshotPath);
            return;
        }

        SetUpTrayAndHotkeys(window);
        _mainViewModel.EnsureOverlayFromSettings();
        _mainViewModel.RestoreStartupState();
        WatchForActivationSignal(window);

        if (_services.Settings.StartMinimizedToTray || args.Contains("--minimized"))
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Hide();
        }
        else
        {
            window.Show();
        }
    }

    private void SetUpTrayAndHotkeys(MainWindow window)
    {
        _tray = new TrayService();
        _tray.OpenRequested += () => RestoreWindow(window);
        _tray.OverlayToggleRequested += () => _mainViewModel?.ToggleOverlay();
        _tray.ResetRequested += () => _mainViewModel?.PanicReset();
        _tray.ExitRequested += () =>
        {
            _exitRequested = true;
            Shutdown();
        };

        _hotkeys = new HotkeyService();
        _hotkeys.OverlayToggle += () => _mainViewModel?.ToggleOverlay();
        _hotkeys.PanicReset += () => _mainViewModel?.PanicReset();
        _hotkeys.ApplyProfileSlot += slot => _mainViewModel?.ApplyProfileSlot(slot);
        _hotkeys.Attach(window);

        if (_mainViewModel is not null)
        {
            _mainViewModel.TrayAlert += (title, message) => _tray?.Alert(title, message);
        }

        _tooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tooltipTimer.Tick += (_, _) =>
        {
            if (_mainViewModel is not null)
            {
                _tray?.UpdateTooltip(_mainViewModel.BuildTrayTooltip());
            }
        };
        _tooltipTimer.Start();

        // Minimize / close behavior.
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized && _services?.Settings.CloseToTray == true)
            {
                window.ShowInTaskbar = false;
                window.Hide();
            }
        };
        window.Closing += (_, e) =>
        {
            if (!_exitRequested && _services?.Settings.CloseToTray == true)
            {
                e.Cancel = true;
                window.Hide();
                window.ShowInTaskbar = false;
            }
            else
            {
                Shutdown();
            }
        };
    }

    private static void RestoreWindow(MainWindow window)
    {
        window.ShowInTaskbar = true;
        window.Show();
        window.WindowState = WindowState.Normal;
        _ = window.Activate();
    }

    private void WatchForActivationSignal(MainWindow window)
    {
        if (_activationSignal is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (!_exitRequested)
            {
                try
                {
                    if (_activationSignal.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        _ = Dispatcher.BeginInvoke(() => RestoreWindow(window));
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Afterglow activation",
        };
        thread.Start();
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException();
            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args),
            };
            _ = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // UAC declined or relaunch failed — continue unelevated (monitoring only).
            return false;
        }
    }

    /// <summary>
    /// Renders the window off-screen after telemetry has accumulated and saves a
    /// PNG. Used for automated visual verification and README screenshots.
    /// --screenshot-delay N extends the accumulation window (e.g., to capture
    /// real graphs while a load runs).
    /// </summary>
    private void RunScreenshotMode(MainWindow window, string path)
    {
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();

        double delaySeconds = 4;
        string[] cliArgs = Environment.GetCommandLineArgs();
        for (int i = 0; i < cliArgs.Length - 1; i++)
        {
            if (cliArgs[i].Equals("--screenshot-delay", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(cliArgs[i + 1], out double parsed))
            {
                delaySeconds = Math.Clamp(parsed, 1, 600);
            }
        }

        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            try
            {
                var bitmap = new RenderTargetBitmap(
                    (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var stream = File.Create(path);
                encoder.Save(stream);
                Console.WriteLine($"Screenshot saved: {path}");
            }
            catch (Exception ex)
            {
                // GPU contention can fail a render pass; retry once before giving up.
                if (attempts < 2)
                {
                    Console.Error.WriteLine($"Screenshot attempt {attempts} failed ({ex.Message}); retrying.");
                    return;
                }

                Console.Error.WriteLine($"Screenshot failed: {ex.Message}");
                Shutdown(1);
                return;
            }

            timer.Stop();
            Shutdown();
        };
        timer.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Afterglow hit an unexpected error:\n\n{e.Exception.Message}\n\nThe app will keep running; " +
            "if tuning was applied you can use Reset to return to driver defaults.",
            "Afterglow", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // A process that never started the services has nothing to mark. WPF's
        // Shutdown() still runs OnExit, so every early exit — the single-instance
        // short-circuit, --register-startup, the self-elevation relaunch — used
        // to rewrite the applied-state record to "clean". A second process
        // launched while the real one is tuning would therefore erase the
        // resident instance's pending record, and the next launch would show no
        // banner for a card that is still tuned. The ACL on %ProgramData%\Afterglow
        // blocks the unelevated case, but the installer's elevated post-install
        // launch is not unelevated. (--demo and --screenshot DO start the
        // services; they are excluded from the mark below via _ephemeralRun.)
        if (_services is null)
        {
            base.OnExit(e);
            return;
        }

        _exitRequested = true;
        _tooltipTimer?.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();

        // Before the clean-shutdown mark and before the GPU services go away: a
        // stepper still in flight has an untested offset applied, and its cancel
        // path restores the offset the run started from. Applying writes a fresh
        // record with CleanShutdown = false, so restoring after MarkCleanShutdown
        // would fake a crash on the next launch.
        // Ask the probe to stop BEFORE waiting on the stepper, as PanicReset
        // does: its cancel is only a flag, and requesting it afterwards left a
        // mid-sweep probe pinning the clock through the whole 15 s stepper wait.
        _mainViewModel?.VfCurve.RequestProbeStop();
        _mainViewModel?.Stability.Dispose();

        // Same hazard, same window: the V/F probe pins the core clock at an
        // EXACT frequency and undoes it only in its worker's finally block. That
        // worker is a background thread, so exiting mid-probe used to kill it
        // without restoring anything and leave the GPU pinned until reboot.
        bool probeClockRestored = _mainViewModel?.VfCurve.CancelProbeAndWait(TimeSpan.FromSeconds(10)) ?? true;

        // An elevated --screenshot run for the README, or a --demo session closed
        // beside a tuning instance, reached this line with services started and
        // rewrote the resident instance's record to "clean". Neither applies
        // anything to real hardware, so neither has anything to mark.
        if (!_ephemeralRun)
        {
            AppliedStateStore.MarkCleanShutdown();
        }

        // Nothing to compose for the probe here: the tuner put each pin on
        // record before it landed and resolves it only on a verified release,
        // so a join that timed out leaves the truthful record behind, and
        // MarkCleanShutdown keeps that flag across the mark.
        if (!probeClockRestored)
        {
            Core.Diagnostics.Log.Warn("The V/F probe was still unwinding at shutdown; its pin record stands.");
        }

        _services?.Dispose();
        _activationSignal?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
