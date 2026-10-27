using System.Runtime.InteropServices;

namespace Afterglow.Core.Metrics;

/// <summary>
/// The two ETW controller calls a PresentMon capture needs from this side
/// (R-29): listing the trace sessions that are running, and stopping one by
/// name. Every capture used to share the session name "Afterglow" and start
/// with --stop_existing_session, which reclaimed an orphan for free but also
/// stopped any OTHER capture running under that name — a CLI fps run ended the
/// GUI's capture. Captures now run under names of their own, so the two jobs
/// the shared name did are done here instead: the session a stopped
/// PresentMon leaves behind is stopped by name, and one left by a host that
/// crashed is found and stopped on the next start.
/// </summary>
internal static unsafe partial class EtwSessions
{
    /// <summary>EVENT_TRACE_PROPERTIES (evntrace.h) with its WNODE_HEADER inlined: 120 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceProperties
    {
        // WNODE_HEADER
        public uint WnodeBufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;

        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    internal enum StopOutcome
    {
        Stopped,
        NotRunning,
        Failed,
    }

    private const uint EventTraceControlStop = 1;
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorWmiInstanceNotFound = 4201;

    /// <summary>QueryAllTraces' documented array limit.</summary>
    private const int MaxSessions = 64;

    /// <summary>ETW's maximum length for a session name and for a log-file name.</summary>
    private const int NameChars = 1024;

    private static int PropertiesBytes => sizeof(EventTraceProperties) + (2 * NameChars * sizeof(char));

    [LibraryImport("advapi32.dll", EntryPoint = "QueryAllTracesW")]
    private static partial int QueryAllTraces(EventTraceProperties** propertyArray, uint propertyArrayCount, out uint loggerCount);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ControlTrace(ulong traceHandle, string instanceName, EventTraceProperties* properties, uint controlCode);

    /// <summary>Sizes the block and places the two name buffers right after the struct, as ETW requires.</summary>
    private static void Prepare(EventTraceProperties* properties)
    {
        properties->WnodeBufferSize = (uint)PropertiesBytes;
        properties->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        properties->LogFileNameOffset = (uint)(sizeof(EventTraceProperties) + (NameChars * sizeof(char)));
    }

    /// <summary>The names of the running trace sessions this process can see; empty when the query fails.</summary>
    public static IReadOnlyList<string> RunningSessionNames()
    {
        int size = PropertiesBytes;
        byte* block = (byte*)NativeMemory.AllocZeroed((nuint)(size * MaxSessions));
        try
        {
            EventTraceProperties** array = stackalloc EventTraceProperties*[MaxSessions];
            for (int i = 0; i < MaxSessions; i++)
            {
                array[i] = (EventTraceProperties*)(block + (i * size));
                Prepare(array[i]);
            }

            // More data: more sessions than the array holds; the first 64 still came back.
            int rc = QueryAllTraces(array, MaxSessions, out uint count);
            if (rc is not ErrorSuccess and not ErrorMoreData)
            {
                return [];
            }

            var names = new List<string>();
            for (int i = 0; i < Math.Min((int)count, MaxSessions); i++)
            {
                uint offset = array[i]->LoggerNameOffset;
                if (offset == 0 || offset + (NameChars * sizeof(char)) > size)
                {
                    continue;
                }

                var chars = new ReadOnlySpan<char>((byte*)array[i] + offset, NameChars);
                int length = chars.IndexOf('\0');
                string name = new(length < 0 ? chars : chars[..length]);
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        finally
        {
            NativeMemory.Free(block);
        }
    }

    /// <summary>Stops the named trace session; a session that is not running is not an error.</summary>
    public static StopOutcome Stop(string sessionName, out int win32Error)
    {
        var properties = (EventTraceProperties*)NativeMemory.AllocZeroed((nuint)PropertiesBytes);
        try
        {
            Prepare(properties);
            win32Error = ControlTrace(0, sessionName, properties, EventTraceControlStop);
            return win32Error switch
            {
                // More data: the session IS stopped; only the properties it
                // reports back did not fit.
                ErrorSuccess or ErrorMoreData => StopOutcome.Stopped,
                ErrorWmiInstanceNotFound => StopOutcome.NotRunning,
                _ => StopOutcome.Failed,
            };
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }
}
