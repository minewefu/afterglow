namespace Afterglow.Core.Services;

/// <summary>
/// Runs an app's shutdown as independent steps (R-05). A step that throws is
/// logged and the next one still runs: the app's teardown used to be a plain
/// sequence, so one failing cleanup — a CSV flush on a full or disconnected
/// drive — skipped everything after it, the fan release included, and left a
/// card pinned at a manual duty after exit. Steps that hand hardware back run
/// as essential, and the sequence remembers whether every one of them
/// succeeded, so the caller stamps the session clean only when it was.
/// </summary>
public sealed class ShutdownSteps
{
    private readonly List<string> _failed = [];

    /// <summary>False once an essential step reported failure or threw.</summary>
    public bool EssentialSucceeded { get; private set; } = true;

    /// <summary>The steps that threw or (essential ones) reported failure, in order.</summary>
    public IReadOnlyList<string> Failed => _failed;

    /// <summary>Runs a cleanup whose failure leaves no hardware changed (files, threads, handles).</summary>
    public void Run(string name, Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Shutdown step '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            _failed.Add(name);
        }
    }

    /// <summary>
    /// Runs a step that hands hardware back. It returns whether it did; false or
    /// an exception makes <see cref="EssentialSucceeded"/> false for good.
    /// </summary>
    public void RunEssential(string name, Func<bool> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        bool succeeded;
        try
        {
            succeeded = step();
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Shutdown step '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            succeeded = false;
        }

        if (!succeeded)
        {
            Diagnostics.Log.Warn($"Shutdown step '{name}' did not release the hardware; the session is not marked clean.");
            _failed.Add(name);
            EssentialSucceeded = false;
        }
    }
}
