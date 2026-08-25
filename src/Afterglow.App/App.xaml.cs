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

        bool demo = args.Contains("--demo");
        string? screenshotPath = GetArgValue(e.Args, "--screenshot");
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
            _services = AppServices.Create(demo);
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
    /// Renders the window off-screen after a few telemetry ticks and saves a PNG.
    /// Used for automated visual verification and README screenshots.
    /// </summary>
    private void RunScreenshotMode(MainWindow window, string path)
    {
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();

        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
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
        _exitRequested = true;
        _tooltipTimer?.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        AppliedStateStore.MarkCleanShutdown();
        _services?.Dispose();
        _activationSignal?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
