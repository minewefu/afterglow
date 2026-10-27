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
    private DispatcherTimer? _tooltipTimer;
    private bool _exitRequested;
    private System.Threading.Mutex? _singleInstanceMutex;
    private System.Threading.Mutex? _hardwareOwnerMutex;
    private System.Threading.EventWaitHandle? _activationSignal;
    private RegisteredWaitHandle? _activationWait;

    public static new App Current => (App)Application.Current;

    public AppServices Services => _services ?? throw new InvalidOperationException("Services not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --data-dir first: every path below (log, settings, flight files, the
        // single-instance name) must already point at the redirected root.
        string[] argv;
        try
        {
            argv = Core.CommandLine.ApplyDataDir(e.Args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Afterglow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var args = new HashSet<string>(argv, StringComparer.OrdinalIgnoreCase);

        if (args.Contains("--present-storm"))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new PresentStormWindow().Show();
            return;
        }

        // Installer hook: register the elevated no-UAC logon task and exit.
        // The installer's post-install step runs elevated, so this succeeds
        // without any prompt of its own. Setup may run as an administrator who
        // only typed a password for the signed-in user, so the task is for the
        // user signed in to this session, not for this process's account (R-41).
        if (args.Contains("--register-startup"))
        {
            string? failure = AppServices.CheckElevated()
                ? StartupTaskService.Enable().Error
                : "the post-install step did not run elevated.";
            if (failure is not null)
            {
                // Setup runs this hidden and can only show a generic message
                // from the exit code; the log keeps the actual reason.
                Core.Diagnostics.Log.Error($"--register-startup did not register the task: {failure}");
            }

            Shutdown(failure is null ? 0 : 1);
            return;
        }

        // Uninstaller hook (R-46): every Windows user has their own startup task,
        // so no fixed name in [UninstallRun] can remove them all. Enumerate and
        // delete every user's task plus the legacy machine-global one, and exit.
        if (args.Contains("--unregister-startup-all-users"))
        {
            string? failure = AppServices.CheckElevated()
                ? (StartupTaskService.RemoveAllUsersTasks() ? null : "not every startup task could be removed.")
                : "the uninstall step did not run elevated.";
            if (failure is not null)
            {
                Core.Diagnostics.Log.Error($"--unregister-startup-all-users: {failure}");
            }

            Shutdown(failure is null ? 0 : 1);
            return;
        }

        bool demo = args.Contains("--demo");
        string? screenshotPath = Core.CommandLine.ValueOf(argv, "--screenshot");
        _ephemeralRun = demo || screenshotPath is not null;
        string? page = Core.CommandLine.ValueOf(argv, "--page");

        // Single instance: two Afterglows would fight over fan control, hotkeys,
        // and the TDR watchdog. A second launch signals the first to come forward.
        // A --data-dir instance is its own instance (own state, own names).
        if (screenshotPath is null && !demo)
        {
            string suffix = Core.AppPaths.InstanceSuffix;
            _singleInstanceMutex = new System.Threading.Mutex(
                initiallyOwned: true, @"Local\AfterglowSingleInstance" + suffix, out bool isFirst);
            _activationSignal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, @"Local\AfterglowActivate" + suffix);
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
            // The --data-dir value goes over already resolved: the elevated copy
            // resolves relative paths against its own working directory.
            if (TryRelaunchElevated(Core.CommandLine.WithResolvedDataDir(e.Args)))
            {
                Shutdown();
                return;
            }
        }

        // At most one Afterglow may hold administrator rights at a time: two
        // would fight over fans, clock locks, the TDR watchdog's resets and game
        // rules. The per-data-root single-instance mutex above lets a --data-dir
        // instance run beside the resident one, so this second guard is what
        // keeps that pair from both driving the hardware. Global\, not Local\:
        // the GPU is one per machine, and fast user switching or a second admin
        // session would otherwise give each session its own "only" owner.
        // Unelevated instances can't write and don't take part.
        if (!demo && screenshotPath is null && AppServices.CheckElevated())
        {
            bool ownsHardware;
            try
            {
                _hardwareOwnerMutex = new System.Threading.Mutex(
                    initiallyOwned: true, @"Global\AfterglowHardwareOwner", out ownsHardware);
            }
            catch (UnauthorizedAccessException)
            {
                // Created by another session's instance with a DACL we can't open:
                // it is owned elsewhere.
                ownsHardware = false;
            }

            if (!ownsHardware)
            {
                _hardwareOwnerMutex?.Dispose();
                _hardwareOwnerMutex = null;
                MessageBox.Show(
                    "Another Afterglow with administrator rights is already controlling the GPU" +
                    (Core.AppPaths.IsRedirected ? " (this copy was started with --data-dir)" : string.Empty) +
                    ". Two would fight over the fans, clock locks and crash recovery, so this copy exits.\n\n" +
                    "Close the other one first, or run this copy with --no-elevate from a normal (unelevated) " +
                    "shell for monitoring only.",
                    "Afterglow", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(3);
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandledException;

        // One log line per launch with where startup time went, so a slow
        // logon can be diagnosed from the log instead of guessed at.
        var startupClock = Stopwatch.StartNew();
        double runtimeMs;
        using (var self = Process.GetCurrentProcess())
        {
            runtimeMs = (DateTime.Now - self.StartTime).TotalMilliseconds;
        }

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

        long servicesMs = startupClock.ElapsedMilliseconds;
        _mainViewModel = new MainViewModel(_services);
        long viewModelsMs = startupClock.ElapsedMilliseconds - servicesMs;
        if (page is not null)
        {
            _mainViewModel.NavigateTo(page);
        }

        long windowStart = startupClock.ElapsedMilliseconds;
        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;
        long windowMs = startupClock.ElapsedMilliseconds - windowStart;
        void LogStartup(string end) => Core.Diagnostics.Log.Info(
            $"Startup: {runtimeMs:F0} ms runtime before OnStartup, then services {servicesMs} ms, " +
            $"view models {viewModelsMs} ms, window {windowMs} ms; {end} at {startupClock.ElapsedMilliseconds} ms.");

        // Pages only draw while the window is actually on screen (ILivePage).
        void SyncWindowVisible() =>
            _services.SetWindowVisible(window.IsVisible && window.WindowState != WindowState.Minimized);
        window.IsVisibleChanged += (_, _) => SyncWindowVisible();
        window.StateChanged += (_, _) => SyncWindowVisible();

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
            LogStartup("resident in the tray");
        }
        else
        {
            window.ContentRendered += (_, _) => LogStartup("first frame");
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

        var hotkeys = Services.Hotkeys;
        hotkeys.OverlayToggle += () => _mainViewModel?.ToggleOverlay();
        hotkeys.PanicReset += () => _mainViewModel?.PanicReset();
        hotkeys.ApplyProfileSlot += slot => _mainViewModel?.ApplyProfileSlot(slot);
        hotkeys.StatusChanged += () => _tray?.SetShortcuts(
            hotkeys.StatusOf(Core.Hotkeys.HotkeyAction.ToggleOverlay),
            hotkeys.StatusOf(Core.Hotkeys.HotkeyAction.PanicReset));
        hotkeys.Attach(window, Services.Settings.Hotkeys);
        Services.SettingsChanged += settings => hotkeys.Apply(settings.Hotkeys);

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

            // A keyboard layout added while Afterglow runs can make a shortcut
            // type a character; Windows sends no message for that, so look here.
            _services?.Hotkeys.RefreshIfLayoutsChanged();
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

        // A registered kernel wait instead of a dedicated thread that woke every
        // second to check for exit: nothing runs until a second launch signals.
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationSignal,
            (_, _) => Dispatcher.BeginInvoke(() => RestoreWindow(window)),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
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
                // Quoted: a plain space join splits any argument holding a space
                // (a --data-dir or --screenshot path) when the elevated copy parses it.
                Arguments = Core.CommandLine.Join(args),
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

        // --screenshot-size WxH renders at another size (design reviews of a
        // whole long page, the minimum window size, README framing).
        string[] cliArgs = Environment.GetCommandLineArgs();
        if (Core.CommandLine.ValueOf(cliArgs, "--screenshot-size") is { } size &&
            size.Split('x', 'X') is [var w, var h] &&
            double.TryParse(w, System.Globalization.CultureInfo.InvariantCulture, out double width) &&
            double.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out double height))
        {
            window.MinWidth = Math.Min(window.MinWidth, width);
            window.MinHeight = Math.Min(window.MinHeight, height);
            window.Width = width;
            window.Height = height;
        }

        window.Show();

        double delaySeconds = 4;
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

        // Every step is independent (R-05): one that throws is logged and the
        // rest still run — above all the services' hardware release, then the
        // mutexes. A plain sequence let any early failure skip the fan release.
        var steps = new Core.Services.ShutdownSteps();
        steps.Run("tooltip timer", () => _tooltipTimer?.Stop());
        steps.Run("hotkeys", () => _services.Hotkeys.Dispose());
        steps.Run("tray icon", () => _tray?.Dispose());

        // Before the clean-shutdown mark and before the GPU services go away: a
        // stepper still in flight has an untested offset applied, and its cancel
        // path restores the offset the run started from. Applying writes a fresh
        // record with CleanShutdown = false, so restoring after MarkCleanShutdown
        // would fake a crash on the next launch. And when the restore did not
        // land (the wait ran out, or the driver refused it), the exit is not
        // clean: the records stay unclean so the next launch's banner says the
        // last tuning may still be applied, instead of an unvalidated offset
        // surviving with nothing to say so (F-27).
        // The V/F probe pins exact clocks the same way: cancel it first so its
        // unwind overlaps the stepper's, then require both to have landed.
        steps.Run("V/F probe stop request", () => _mainViewModel?.VfCurve.RequestProbeStop());
        steps.RunEssential("stability stepper", () => _mainViewModel?.Stability.StopForShutdown() ?? true);
        steps.RunEssential("V/F probe", () => _mainViewModel?.VfCurve.StopForShutdown() ?? true);
        steps.Run("stability page", () => _mainViewModel?.Stability.Dispose());

        // The services hand the fans back BEFORE the clean-shutdown mark, and
        // the mark waits on them (R-05). Marking first stamped the session clean
        // even when the release never ran (a teardown step threw ahead of it)
        // or the driver refused it, and the next launch showed no banner for a
        // card left pinned — possibly stopped.
        steps.RunEssential("services", _services.Shutdown);

        // Only the resident instance may vouch for a clean exit: a --demo or
        // --screenshot run starts services but applies nothing, and a second
        // launch that just signalled the first holds no instance mutex — either
        // would otherwise mark the RUNNING instance's records clean and erase
        // its crash evidence. The instance mutex is still held here.
        if (steps.EssentialSucceeded && !_ephemeralRun && _singleInstanceMutex is not null)
        {
            steps.Run("clean-shutdown mark", () => AppliedStateStore.MarkCleanShutdown());
        }

        steps.Run("activation wait", () => _activationWait?.Unregister(null));
        steps.Run("activation signal", () => _activationSignal?.Dispose());
        steps.Run("single-instance mutex", () => _singleInstanceMutex?.Dispose());
        steps.Run("hardware-owner mutex", () => _hardwareOwnerMutex?.Dispose());
        base.OnExit(e);
    }
}
