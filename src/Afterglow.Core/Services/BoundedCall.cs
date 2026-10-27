using System.Runtime.ExceptionServices;

namespace Afterglow.Core.Services;

/// <summary>
/// Runs a call that may block inside a GPU driver with a limit on how long the
/// caller waits for it. A native call cannot be cancelled, so the call runs on a
/// thread of its own and is abandoned if it overruns. That thread is a
/// background thread: .NET does not keep a process running for background
/// threads, so a call that never returns cannot keep the app from exiting.
/// For shutdown only — an abandoned call is still inside the driver, and the
/// caller must not issue more calls that could block behind it.
/// </summary>
public static class BoundedCall
{
    /// <summary>
    /// Runs <paramref name="call"/> and waits up to <paramref name="timeout"/>.
    /// Returns false, with <paramref name="result"/> unset, when the call has not
    /// returned by then. An exception the call threw is rethrown here.
    /// </summary>
    public static bool TryRun<T>(Func<T> call, TimeSpan timeout, string name, out T result)
    {
        ArgumentNullException.ThrowIfNull(call);
        T value = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = call();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            Name = name,
            IsBackground = true,
        };
        thread.Start();

        // Join orders the thread's writes before the reads below.
        if (!thread.Join(timeout))
        {
            result = default!;
            return false;
        }

        error?.Throw();
        result = value;
        return true;
    }

    /// <summary><see cref="TryRun{T}"/> for a call with no result.</summary>
    public static bool TryRun(Action call, TimeSpan timeout, string name)
    {
        ArgumentNullException.ThrowIfNull(call);
        return TryRun(() =>
        {
            call();
            return true;
        }, timeout, name, out _);
    }
}
