using System.Globalization;
using System.Text;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// Minimal diagnostic logging: a rotating file under the logs directory plus an
/// in-memory ring for support. Never throws; logging failures are swallowed.
/// </summary>
public static class Log
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private const int RingCapacity = 400;

    private static readonly object Lock = new();
    private static readonly Queue<string> Ring = new();
    private static StreamWriter? _writer;
    private static bool _initFailed;

    public static string LogFile { get; } = Path.Combine(AppPaths.LogsDir, "afterglow.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    /// <summary>Most recent log lines (newest last) for in-app display.</summary>
    public static IReadOnlyList<string> Recent()
    {
        lock (Lock)
        {
            return Ring.ToArray();
        }
    }

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}");

        lock (Lock)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity)
            {
                _ = Ring.Dequeue();
            }

            try
            {
                if (_writer is null && !_initFailed)
                {
                    AppPaths.EnsureCreated();
                    if (File.Exists(LogFile) && new FileInfo(LogFile).Length > MaxBytes)
                    {
                        File.Copy(LogFile, LogFile + ".old", overwrite: true);
                        File.Delete(LogFile);
                    }

                    _writer = new StreamWriter(LogFile, append: true, Encoding.UTF8) { AutoFlush = true };
                }

                _writer?.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _initFailed = true;
                _writer = null;
            }
        }
    }
}
