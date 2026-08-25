using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Afterglow.App.Services;

/// <summary>
/// Tray icon with quick actions: open, toggle overlay, reset to defaults, exit.
/// Also carries temperature alerts as balloon notifications.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private DateTime _lastAlert = DateTime.MinValue;

    public event Action? OpenRequested;

    public event Action? OverlayToggleRequested;

    public event Action? ResetRequested;

    public event Action? ExitRequested;

    public TrayService()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Afterglow", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Toggle overlay (Ctrl+Alt+O)", null, (_, _) => OverlayToggleRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Reset GPU to defaults (Ctrl+Alt+R)", null, (_, _) => ResetRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Text = "Afterglow",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "afterglow.ico");
        if (File.Exists(path))
        {
            return new System.Drawing.Icon(path);
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void UpdateTooltip(string text)
    {
        // NotifyIcon tooltips cap at 127 chars.
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>Balloon alert, rate-limited to one per minute.</summary>
    public void Alert(string title, string message)
    {
        if (DateTime.UtcNow - _lastAlert < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastAlert = DateTime.UtcNow;
        _icon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
